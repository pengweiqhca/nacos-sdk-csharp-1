﻿namespace Nacos.Microsoft.Extensions.Configuration
{
    using global::Microsoft.Extensions.Configuration;
    using global::Microsoft.Extensions.Logging;
    using Nacos.V2;
    using Nacos.V2.Utils;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    internal class NacosV2ConfigurationProvider : ConfigurationProvider, IDisposable
    {
        private readonly NacosV2ConfigurationSource _configurationSource;

        private readonly INacosConfigurationParser _parser;

        private readonly INacosConfigService _client;

        private readonly ConcurrentDictionary<string, string> _configDict;

        private readonly Dictionary<string, MsConfigListener> _listenerDict;

        private readonly ILogger _logger;

        public NacosV2ConfigurationProvider(NacosV2ConfigurationSource configurationSource, INacosConfigService client, ILoggerFactory loggerFactory)
        {
            _configurationSource = configurationSource;
            _parser = configurationSource.NacosConfigurationParser;
            _configDict = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _listenerDict = new Dictionary<string, MsConfigListener>();

            _client = client;
            _logger = loggerFactory?.CreateLogger<NacosV2ConfigurationProvider>();

            if (configurationSource.Listeners != null && configurationSource.Listeners.Any())
            {
                var tasks = new List<Task>();

                foreach (var item in configurationSource.Listeners)
                {
                    var listener = new MsConfigListener(item, this, _logger);

                    tasks.Add(item.Namespace.IsNullOrWhiteSpace()
                        ? _client.AddListener(item.DataId, item.Group, listener)
                        : _client.AddListener(item.DataId, item.Group, item.Namespace, listener));

                    _listenerDict.Add($"{item.DataId}#{item.Group}#{item.Namespace}", listener);
                }

                Task.WaitAll(tasks.ToArray());
            }
            else
            {
                // after remove old v1 code, Listeners must be not empty
                throw new Nacos.V2.Exceptions.NacosException("Listeners is empty!!");
            }
        }

        internal IDictionary<string, string> GetData() => Data;

        public void Dispose()
        {
            var tasks = new List<Task>();

            foreach (var item in _listenerDict)
            {
                var arr = item.Key.Split('#');
                var dataId = arr[0];
                var group = arr[1];
                var tenant = arr[2];

                tasks.Add(tenant.IsNullOrWhiteSpace()
                    ? _client.RemoveListener(dataId, group, item.Value)
                    : _client.RemoveListener(dataId, group, tenant, item.Value));
            }

            Task.WaitAll(tasks.ToArray());

            _logger?.LogInformation($"Remove All Listeners");
        }

        public override void Load()
        {
            try
            {
                if (_configurationSource.Listeners != null && _configurationSource.Listeners.Any())
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var listener in _configurationSource.Listeners)
                    {
                        try
                        {
                            var config = (listener.Namespace.IsNullOrWhiteSpace()
                                    ? _client.GetConfig(listener.DataId, listener.Group, 3000)
                                    : _client.GetConfig(listener.DataId, listener.Group, listener.Namespace, 3000))
                                .ConfigureAwait(false).GetAwaiter().GetResult();

                            _configDict.AddOrUpdate(GetCacheKey(listener), config, (x, y) => config);

                            var data = _parser.Parse(config);

                            foreach (var item in data)
                            {
                                dict[item.Key] = item.Value;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "MS Config Query config error, dataid={0}, group={1}, tenant={2}", listener.DataId, listener.Group, _configurationSource.GetNamespace());
                            if (!listener.Optional)
                            {
                                throw;
                            }
                        }
                    }

                    Data = dict;
                }
                else
                {
                    // after remove old v1 code, Listeners must be not empty
                    throw new Nacos.V2.Exceptions.NacosException("Listeners is empty!!");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Load config error");
            }
        }

        private string GetCacheKey(ConfigListener listener)
            => $"{(listener.Namespace.IsNullOrWhiteSpace() ? _configurationSource.GetNamespace() : listener.Namespace)}#{listener.Group}#{listener.DataId}";

        // for test
        internal void SetListener(string key, MsConfigListener listener)
        {
            _listenerDict[key] = listener;
        }

        internal class MsConfigListener : IListener
        {
            private bool _optional;
            private NacosV2ConfigurationProvider _provider;
            private string _key;
            private ILogger _logger;

            internal MsConfigListener(ConfigListener listener, NacosV2ConfigurationProvider provider, ILogger logger)
            {
                this._optional = listener.Optional;
                this._provider = provider;
                this._logger = logger;
                _key = provider.GetCacheKey(listener);
            }


            public void ReceiveConfigInfo(string configInfo)
            {
                _logger?.LogDebug("MsConfigListener Receive ConfigInfo 【{0}】", configInfo);
                try
                {
                    _provider._configDict.AddOrUpdate(_key, configInfo, (x, y) => configInfo);

                    var nData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var listener in _provider._configurationSource.Listeners)
                    {
                        var key = _provider.GetCacheKey(listener);

                        if (!_provider._configDict.TryGetValue(key, out var config))
                        {
                            continue;
                        }

                        var data = _provider._parser.Parse(config);

                        foreach (var item in data)
                        {
                            nData[item.Key] = item.Value;
                        }
                    }

                    _provider.Data = nData;
                    _provider.OnReload();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, $"call back reload config error");
                    if (!_optional)
                    {
                        throw;
                    }
                }
            }
        }
    }
}
