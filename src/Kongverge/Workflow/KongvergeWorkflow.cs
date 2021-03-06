using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kongverge.DTOs;
using Kongverge.Helpers;
using Kongverge.Services;
using Microsoft.Extensions.Options;
using Serilog;

namespace Kongverge.Workflow
{
    public class KongvergeWorkflow : Workflow
    {
        private readonly IKongAdminWriter _kongWriter;
        private readonly ConfigFileReader _configReader;
        private readonly ConfigBuilder _configBuilder;

        private OperationStats _createdStats;
        private OperationStats _updatedStats;
        private OperationStats _deletedStats;

        public KongvergeWorkflow(
            IKongAdminReader kongReader,
            IOptions<Settings> configuration,
            IKongAdminWriter kongWriter,
            ConfigFileReader configReader,
            ConfigBuilder configBuilder) : base(kongReader, configuration)
        {
            _kongWriter = kongWriter;
            _configReader = configReader;
            _configBuilder = configBuilder;
        }

        public override async Task<int> DoExecute()
        {
            KongvergeConfiguration targetConfiguration;
            try
            {
                targetConfiguration = await _configReader.ReadConfiguration(Configuration.InputFolder);
            }
            catch (DirectoryNotFoundException ex)
            {
                return ExitWithCode.Return(ExitCode.InputFolderUnreachable, ex.Message);
            }
            catch (InvalidConfigurationFileException ex)
            {
                return ExitWithCode.Return(ExitCode.InvalidConfigurationFile, $"Invalid configuration file {ex.Path}{Environment.NewLine}{ex.Message}");
            }

            var existingConfiguration = await _configBuilder.FromKong(KongReader);
            
            _createdStats = new OperationStats();
            _updatedStats = new OperationStats();
            _deletedStats = new OperationStats();

            await ConvergeChildrenPlugins(null, existingConfiguration.GlobalConfig, targetConfiguration.GlobalConfig);
            
            await ConvergeObjects(
                null,
                KongService.ObjectName,
                existingConfiguration.Services,
                targetConfiguration.Services,
                x => _kongWriter.DeleteService(x.Id),
                x => _kongWriter.AddService(x),
                x => _kongWriter.UpdateService(x),
                ConvergeServiceChildren);

            Log.Information($"Created {_createdStats}");
            Log.Information($"Updated {_updatedStats}");
            Log.Information($"Deleted {_deletedStats}");

            return ExitWithCode.Return(ExitCode.Success);
        }

        private async Task ConvergeObjects<T>(
            string parent,
            string objectName,
            IReadOnlyCollection<T> existingObjects,
            IReadOnlyCollection<T> targetObjects,
            Func<T, Task> deleteObject,
            Func<T, Task> createObject,
            Func<T, Task> updateObject = null,
            Func<T, T, Task> recurse = null) where T : KongObject, IKongEquatable
        {
            existingObjects = existingObjects ?? Array.Empty<T>();
            updateObject = updateObject ?? (x => Task.CompletedTask);
            recurse = recurse ?? ((e, t) => Task.CompletedTask);

            if (existingObjects.Count == 0 && targetObjects.Count == 0)
            {
                Log.Verbose($"Target {parent ?? "configuration"} and existing both have zero {GetName(0, objectName)}");
                return;
            }

            var targetPhrase = $"{targetObjects.Count} target {GetName(targetObjects.Count, objectName)}";
            var existingPhrase = $"{existingObjects.Count} existing {GetName(existingObjects.Count, objectName)}";
            var parentPhrase = parent == null ? string.Empty : $" attached to {parent}";
            Log.Verbose($"Converging {targetPhrase}{parentPhrase} with {existingPhrase}");

            var targetMatchValues = targetObjects.Select(x => x.GetMatchValue()).ToArray();
            var toRemove = existingObjects.Where(x => !targetMatchValues.Contains(x.GetMatchValue())).ToArray();

            foreach (var existing in toRemove)
            {
                Log.Verbose($"Deleting {objectName} {existing}{parentPhrase} which exists in Kong but not in target configuration");
                await deleteObject(existing);
                _deletedStats.Increment<T>();
            }

            foreach (var target in targetObjects)
            {
                var existing = target.MatchWithExisting(existingObjects);
                if (existing == null)
                {
                    Log.Verbose($"Creating {objectName} {target}{parentPhrase} which exists in target configuration but not in Kong");
                    await createObject(target);
                    _createdStats.Increment<T>();
                }
                else if (target.Equals(existing))
                {
                    Log.Verbose($"Identical {objectName} {existing}{parentPhrase} found in Kong matching target configuration");
                }
                else
                {
                    var patch = target.DifferencesFrom(existing);
                    Log.Verbose($"Updating {objectName} {existing}{parentPhrase} which exists in both Kong and target configuration, having the following differences:{Environment.NewLine}{patch}");
                    await updateObject(target);
                    _updatedStats.Increment<T>();
                }
                await recurse(existing, target);
            }
        }

        private Task ConvergeChildrenPlugins(string parent, IKongPluginHost existing, IKongPluginHost target)
        {
            Task UpsertPlugin(KongPlugin plugin, IKongPluginHost host)
            {
                host.AssignParentId(plugin);
                return _kongWriter.UpsertPlugin(plugin);
            }

            return ConvergeObjects(
                parent,
                KongPlugin.ObjectName,
                existing?.Plugins,
                target.Plugins,
                x => _kongWriter.DeletePlugin(x.Id),
                x => UpsertPlugin(x, target),
                x => UpsertPlugin(x, target));
        }

        private async Task ConvergeServiceChildren(KongService existing, KongService target)
        {
            var parent = $"{KongService.ObjectName} {target}";
            await ConvergeChildrenPlugins(parent, existing, target);
            await ConvergeObjects(
                parent,
                KongRoute.ObjectName,
                existing?.Routes,
                target.Routes,
                x => _kongWriter.DeleteRoute(x.Id),
                x => _kongWriter.AddRoute(target.Id, x),
                null,
                (e, t) => ConvergeChildrenPlugins($"{KongRoute.ObjectName} {t} attached to {KongService.ObjectName} {target}", e, t));
        }

        private static string GetName(int count, string singular)
        {
            return count == 1
                ? singular
                : singular + "s";
        }

        private class OperationCount
        {
            public OperationCount(string objectName)
            {
                ObjectName = objectName;
            }

            public string ObjectName { get; }
            public int Count { get; set; }
        }

        private class OperationStats : Dictionary<Type, OperationCount>
        {
            public OperationStats()
            {
                Add(typeof(KongService), new OperationCount(KongService.ObjectName));
                Add(typeof(KongPlugin), new OperationCount(KongPlugin.ObjectName));
                Add(typeof(KongRoute), new OperationCount(KongRoute.ObjectName));
            }

            public void Increment<T>()
            {
                this[typeof(T)].Count++;
            }

            public override string ToString()
            {
                return string.Join(", ", Keys.Select(x => $"{this[x].Count} {GetName(this[x].Count, this[x].ObjectName)}"));
            }
        }
    }
}
