using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimulatedTemperatureSensor
{
    public static class TwinCollectionExtensions
    {
        public static bool TryGetValue<T>(this TwinCollection collection, string propertyName, out T result, T defaultVAlue = default(T))
        {
            bool found = false;
            result = defaultVAlue;
            if (collection.Contains(propertyName))
            {
                found = true;
                JObject propertyJson = collection[propertyName] as JObject;
                if (propertyJson != null)
                {
                    if (propertyJson.ContainsKey("value"))
                    {
                        var propertyValue = propertyJson["value"];
                        result = propertyValue.Value<T>();
                    }
                }
                else
                {
                    result = collection[propertyName];
                }
            }
            return found;
        }
    }
}
