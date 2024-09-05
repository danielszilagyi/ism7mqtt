﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using ism7mqtt.ISM7.Xml;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ism7mqtt.ISM7.Config;

namespace ism7mqtt.HomeAssistant
{
    public class HaDiscovery
    {
        private readonly Ism7Config _config;
        private readonly IMqttClient _mqttClient;
        private readonly string _discoveryId;

        public bool EnableDebug { get; set; }

        public MqttQualityOfServiceLevel QosLevel { get; set; }

        public HaDiscovery(Ism7Config config, IMqttClient mqttClient, string discoveryId)
        {
            _config = config;
            _mqttClient = mqttClient;
            _discoveryId = discoveryId;
        }

        public async Task PublishDiscoveryInfo(CancellationToken cancellationToken)
        {
            if (EnableDebug)
            {
                Console.WriteLine($"Publishing HA Discovery info for ID {_discoveryId}");
            }
            foreach (var message in GetDiscoveryInfo())
            {
                var data = JsonSerializer.Serialize(message.Content, JsonContext.Default.JsonObject);
                var builder = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Path)
                    .WithPayload(data)
                    .WithQualityOfServiceLevel(QosLevel);
                var payload = builder
                    .Build();
                await _mqttClient.PublishAsync(payload, cancellationToken);
            }
        }

        public IEnumerable<JsonMessage> GetDiscoveryInfo()
        {
            return _config.Devices.SelectMany(x => GetDiscoveryInfo(x));
        }

        private string LaunderHomeassistantId(string id) {
            id = id.Replace("ä", "ae")
                .Replace("ö", "oe")
                .Replace("ü", "ue")
                .Replace("Ä", "Ae")
                .Replace("Ö", "Oe")
                .Replace("Ü", "Ue")
                .Replace("ß", "ss")
                .Replace(' ', '_');
            return Regex.Replace(id, "[^a-zA-Z0-9_ ]+", String.Empty, RegexOptions.Compiled);
        }

        private string ToHaObjectId(Ism7Config.RunningDevice device, ParameterDescriptor parameter)
        {
            var objectId = $"{_discoveryId}_{device.Name}_{device.WriteAddress}_{parameter.PTID}_{parameter.Name}";
            return LaunderHomeassistantId(objectId);
        }

        private IEnumerable<JsonMessage> GetDiscoveryInfo(Ism7Config.RunningDevice device)
        {
            List<JsonMessage> result = new List<JsonMessage>();
            foreach (var parameter in device.Parameters)
            {
                var descriptor = parameter.Descriptor;
                var type = GetHomeAssistantType(descriptor);
                if (type == null) continue;
                if (descriptor.ControlType == "DaySwitchTimes" || descriptor.ControlType.Contains("NO_DISPLAY")) continue;
                var uniqueId = ToHaObjectId(device, descriptor);

                // Handle parameters with name-duplicates - their ID will be part of the topic
                string deduplicator = "";
                string deduplicatorLabel = "";
                if (parameter.IsDuplicate)
                {
                    deduplicator = $"/{descriptor.PTID}";
                    deduplicatorLabel = $"_{descriptor.PTID}";
                }
                
                string discoveryTopic = $"homeassistant/{type}/{uniqueId}/config";
                var message = new JsonObject();
                message.Add("unique_id", uniqueId);

                var discoveryTopicSSuffix = GetDiscoveryTopicSuffix(descriptor);
                string stateTopic = $"{device.MqttTopic}/{parameter.MqttName}{deduplicator}{discoveryTopicSSuffix}";
                message.Add("state_topic", stateTopic);

                if (descriptor.IsWritable)
                {
                    string commandTopic = $"{device.MqttTopic}/set/{parameter.MqttName}{deduplicator}{discoveryTopicSSuffix}";
                    message.Add("command_topic", commandTopic);
                }

                message.Add("name", descriptor.Name);
                message.Add("object_id", uniqueId);

                foreach (var (key, value) in GetDiscoveryProperties(descriptor))
                {
                    message.Add(key, value);
                }

                // Try to guess a suitable icon if none given
                if (!message.ContainsKey("icon"))
                {
                    if (descriptor.Name.ToLower().Contains("brenner"))
                        message.Add("icon", "mdi:fire");
                    else if (descriptor.Name.ToLower().Contains("solar"))
                        message.Add("icon", "mdi:solar-panel");
                    else if (descriptor.Name.ToLower().Contains("ventil"))
                        message.Add("icon", "mdi:pipe-valve");
                    else if (descriptor.Name.ToLower().Contains("heizung"))
                        message.Add("icon", "mdi:radiator");
                    else if (descriptor.Name.ToLower().Contains("pumpe"))
                        message.Add("icon", "mdi:pump");
                    if (descriptor.Name.ToLower().Contains("druck"))
                        message.Add("icon", "mdi:gauge");
                }

                message.Add("device", GetDiscoveryDeviceInfo(device));
                result.Add(new JsonMessage(discoveryTopic, message));
            }
            return result;
        }

        private JsonObject GetDiscoveryDeviceInfo(Ism7Config.RunningDevice device)
        {
            return new JsonObject
            {
                { "configuration_url", $"http://{device.IP}/" },
                { "manufacturer", "Wolf" },
                { "model", device.Name },
                { "name", $"{_discoveryId} {device.Name}" },
                {
                    "connections", new JsonArray
                    (
                        new JsonArray
                        (
                            "ip_dev",
                            $"{device.IP}_{device.Name}"
                        )
                    )
                }
            };
        }

        private string GetDiscoveryTopicSuffix(ParameterDescriptor descriptor)
        {
            if (descriptor is ListParameterDescriptor list)
            {
                return "/text";
            }
            else
            {
                return String.Empty;
            }
        }

        private string GetHomeAssistantType(ParameterDescriptor descriptor)
        {
            switch (descriptor)
            {
                case ListParameterDescriptor list:
                    // Try to auto-detect switch and select vs sensor and binary_sensor
                    if (list.IsWritable)
                    {
                        return list.IsBoolean ? "switch" : "select";
                    }

                    return list.IsBoolean ? "binary_sensor" : "sensor";
                case NumericParameterDescriptor numeric:
                    return numeric.IsWritable ? "number" : "sensor";
                case TextParameterDescriptor text:
                    return text.IsWritable ? "text" : "sensor";
                case OtherParameterDescriptor other:
                    return other.IsWritable ? "text" : "sensor";
                default:
                    return null;
            }
        }

        private IEnumerable<(string, JsonNode)> GetDiscoveryProperties(ParameterDescriptor descriptor)
        {
            switch (descriptor)
            {
                case NumericParameterDescriptor numeric:
                    if (numeric.IsWritable)
                    {
                        if (numeric.MinValueCondition != null)
                        {
                            if (Double.TryParse(numeric.MinValueCondition, NumberStyles.Number, CultureInfo.InvariantCulture, out var min))
                            {
                                yield return("min", min);
                            }
                            else if (EnableDebug)
                            {
                                Console.WriteLine($"Cannot parse MinValueCondition '{numeric.MinValueCondition}' for PTID {descriptor.PTID}");
                            }
                        }
                        if (numeric.MaxValueCondition != null)
                        {
                            if (Double.TryParse(numeric.MaxValueCondition, NumberStyles.Number, CultureInfo.InvariantCulture, out var max))
                            {
                                yield return ("max", max);
                            }
                            else if (EnableDebug)
                            {
                                Console.WriteLine($"Cannot parse MaxValueCondition '{numeric.MaxValueCondition}' for PTID {descriptor.PTID}");
                            }
                        }
                        if (numeric.StepWidth != null)
                            yield return ("step", numeric.StepWidth);
                    }
                    if (numeric.UnitName != null)
                    {
                        yield return ("unit_of_measurement", numeric.UnitName);
                        if (numeric.UnitName == "°C")
                        {
                            yield return ("icon", "mdi:thermometer");
                            yield return ("state_class", "measurement");
                        }
                        else if (numeric.UnitName == "%")
                        {
                            yield return ("state_class", "measurement");
                        }
                    }
                    break;
                case ListParameterDescriptor list:
                    if (list.IsBoolean)
                    {
                        if (list.Options.Any(x => x.Value == "Ein"))
                        {
                            yield return ("payload_on", "Ein");
                        }
                        if (list.Options.Any(x => x.Value == "Aktiviert"))
                        {
                            yield return ("payload_on", "Aktiviert");
                        }
                        if (list.Options.Any(x => x.Value == "Aus"))
                        {
                            yield return ("payload_off", "Aus");
                        }
                        if (list.Options.Any(x => x.Value == "Deaktiviert"))
                        {
                            yield return ("payload_off", "Deaktiviert");
                        }
                    }
                    else
                    {
                        var options = new JsonArray();
                        foreach (var value in list.Options)
                        {
                            options.Add((JsonNode)value.Value);
                        }
                        yield return ("options", options);
                        yield return ("device_class", "enum");

                    }
                    break;
                default:
                    yield break;
            }
        }
    }
}
