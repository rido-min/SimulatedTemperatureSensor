namespace SimulatedTemperatureSensor
{
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;
    using System;
    using System.Net;
    using System.Runtime.Loader;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    class Program
    {
        static int counter;

        static int SendInterval;
        static bool SendData;
        static TimeSpan messageDelay;
        static SimulatorParameters SimulatorParameters;

        static readonly Guid BatchId = Guid.NewGuid();
        static readonly AtomicBoolean Reset = new AtomicBoolean(false);
        static readonly Random Rnd = new Random();
        static CancellationTokenSource cts = new CancellationTokenSource();
        
        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ClientOptions options = new ClientOptions() { ModelId = "dtmi:rido:sts;1" };
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings, options);
            //ModuleClient ioTHubModuleClient = ModuleClient.CreateFromConnectionString(Environment.GetEnvironmentVariable("CS"), settings, options);

            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            SendData = true; // await ReadTwinPropertyAsync<bool>("SendData", ioTHubModuleClient);
            SendInterval = 3; // await ReadTwinPropertyAsync<int>("SendInterval", ioTHubModuleClient);
            messageDelay = TimeSpan.FromSeconds(SendInterval);
            //SimulatorParameters = await ReadTwinPropertyAsync<SimulatorParameters>("SimulatorParameters", ioTHubModuleClient);
            SimulatorParameters = new SimulatorParameters
            {
                AmbientTemp = 10,
                HumidityPercent = 80,
                MachinePressureMax = 100,
                MachinePressureMin = 10,
                MachineTempMax = 50,
                MachineTempMin = 20
            };

            Console.WriteLine($"InitValues: SendData:${SendData} SendInterval: ${SendInterval}");


            ModuleClient userContext = ioTHubModuleClient;
            await ioTHubModuleClient.SetMethodHandlerAsync("reset", ResetMethod, null);
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdated, userContext);
            await SendEvents(ioTHubModuleClient, 500, SimulatorParameters, cts);


            // Register callback to be called when a message is received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", PipeMessage, ioTHubModuleClient);
        }

        static Task<MethodResponse> ResetMethod(MethodRequest methodRequest, object userContext)
        {
            Console.WriteLine("Received direct method call to reset temperature sensor...");
            Reset.Set(true);
            var response = new MethodResponse((int)HttpStatusCode.OK);
            return Task.FromResult(response);
        }

        static async Task OnDesiredPropertiesUpdated(TwinCollection desiredPropertiesPatch, object userContext)
        {
            Console.WriteLine("Writable props received:" + desiredPropertiesPatch);
            var ack = new TwinCollection();// $"{{ \"SendData\":{SendData.ToString().ToLower()}, \"SendInterval\": {messageDelay.TotalSeconds}}}");
            // At this point just update the configure configuration.
            if (desiredPropertiesPatch.Contains("SendInterval"))
            {
                if (desiredPropertiesPatch.TryGetValue<int>("SendInterval", out SendInterval))
                {
                    messageDelay = TimeSpan.FromSeconds(SendInterval);
                    ack["SendInterval"] = new
                    {
                        ac = 200,
                        av = desiredPropertiesPatch.Version,
                        value = SendInterval
                    };
                }
                
            }

            if (desiredPropertiesPatch.Contains("SendData"))
            {
                bool desiredSendDataValue = (bool)desiredPropertiesPatch["SendData"];
                SendData = desiredSendDataValue;
                if (!SendData)
                {
                    Console.WriteLine("Sending data disabled. Change twin configuration to start sending again.");
                }
                ack["SendData"] = new
                {
                    ac = 200,
                    av = desiredPropertiesPatch.Version,
                    value = SendData
                };

            }

            var moduleClient = (ModuleClient)userContext;
            await moduleClient.UpdateReportedPropertiesAsync(ack); // Just report back last desired property.
        }

        static async Task SendEvents(
           ModuleClient moduleClient,
           int messageCount,
           SimulatorParameters sim,
           CancellationTokenSource cts)
        {
            int count = 1;
            double currentTemp = sim.MachineTempMin;
            double normal = (sim.MachinePressureMax - sim.MachinePressureMin) / (sim.MachineTempMax - sim.MachineTempMin);

            static bool SendUnlimitedMessages(int maximumNumberOfMessages) => maximumNumberOfMessages < 0;

            while (!cts.Token.IsCancellationRequested && (SendUnlimitedMessages(messageCount) || messageCount >= count))
            {
                if (Reset)
                {
                    currentTemp = sim.MachineTempMin;
                    Reset.Set(false);
                }

                if (currentTemp > sim.MachineTempMax)
                {
                    currentTemp += Rnd.NextDouble() - 0.5; // add value between [-0.5..0.5]
                }
                else
                {
                    currentTemp += -0.25 + (Rnd.NextDouble() * 1.5); // add value between [-0.25..1.25] - average +0.5
                }

                if (SendData)
                {
                    var tempData = new MessageBody
                    {
                        Machine = new Machine
                        {
                            Temperature = currentTemp,
                            Pressure = sim.MachinePressureMin + ((currentTemp - sim.MachineTempMin) * normal),
                        },
                        Ambient = new Ambient
                        {
                            Temperature = sim.AmbientTemp + Rnd.NextDouble() - 0.5,
                            Humidity = Rnd.Next(24, 27)
                        },
                        TimeCreated = DateTime.UtcNow
                    };

                    string dataBuffer = JsonConvert.SerializeObject(tempData);
                    var eventMessage = new Message(Encoding.UTF8.GetBytes(dataBuffer));
                    eventMessage.Properties.Add("sequenceNumber", count.ToString());
                    eventMessage.Properties.Add("batchId", BatchId.ToString());
                    Console.WriteLine($"\t{DateTime.Now.ToLocalTime()}> Sending message: {count}, Body: [{dataBuffer}]");

                    await moduleClient.SendEventAsync("temperatureOutput", eventMessage);
                    count++;
                }

                await Task.Delay(messageDelay, cts.Token);
            }

            if (messageCount < count)
            {
                Console.WriteLine($"Done sending {messageCount} messages");
            }
        }

        private static async Task<T> ReadTwinPropertyAsync<T>(string propName, ModuleClient mc)
        {
            var twin = await mc.GetTwinAsync();
            T result;
            int status;
            string description;
            if (twin.Properties.Desired.Contains(propName))
            {
                if (twin.Properties.Desired.TryGetValue<T>(propName, out result))
                {
                    status = 200;
                    description = "Found in desired";
                }
                else
                {
                    Console.WriteLine("Cannot parse desired. " + propName);
                    status = 501;
                    description = "Found in desired but cannot parse";
                    result = default(T);
                }
            }
            else // prop not available in desired, try in reported
            {
                if (twin.Properties.Reported.Contains(propName))
                {
                    if (twin.Properties.Reported.TryGetValue<T>(propName, out result))
                    {
                        description = "Found in reported";
                        status = 202;
                    }
                    else
                    {
                        Console.WriteLine("Cannot parse reported. " + propName);
                        status = 502;
                        description = "Found in reported but cannot parse";
                        result = default(T);
                    }
                }
                else
                {
                    Console.WriteLine("prop not found:" + propName);
                    description = "prop not found, applying defaults";
                    status = 100;
                    result = default(T);
                }
            }
            var ack = new TwinCollection();
            ack[propName] = new
            {
                ac = status,
                av = (status == 200) ? twin.Properties.Desired.Version : 1,
                ad = description,
                value = result

            };
            await mc.UpdateReportedPropertiesAsync(ack);
            return result;
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        static async Task<MessageResponse> PipeMessage(Message message, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);

            var moduleClient = userContext as ModuleClient;
            if (moduleClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Console.WriteLine($"Received message: {counterValue}, Body: [{messageString}]");

            if (!string.IsNullOrEmpty(messageString))
            {
                using (var pipeMessage = new Message(messageBytes))
                {
                    foreach (var prop in message.Properties)
                    {
                        pipeMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    await moduleClient.SendEventAsync("output1", pipeMessage);
                
                    Console.WriteLine("Received message sent");
                }
            }
            return MessageResponse.Completed;
        }
    }
}
