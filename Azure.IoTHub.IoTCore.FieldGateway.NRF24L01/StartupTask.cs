//---------------------------------------------------------------------------------
// Copyright ® December 2017, devMobile Software
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Thanks to the creators and maintainers of the library used by this project
//    https://github.com/techfooninja/Radios.RF24
//---------------------------------------------------------------------------------
namespace devMobile.Azure.IoTHub.IoTCore.FieldGateway.NRF24L01
{
   using System;
   using System.Diagnostics;
   using System.IO;
   using System.Text;
   using System.Threading.Tasks;
   using Microsoft.Azure.Devices.Client;
   using Newtonsoft.Json;
   using Newtonsoft.Json.Converters;
   using Newtonsoft.Json.Linq;
   using Radios.RF24;
   using Windows.ApplicationModel.Background;
   using Windows.Foundation.Diagnostics;
   using Windows.Storage;

   public sealed class StartupTask : IBackgroundTask
   {
      private const string ConfigurationFilename = "config.json";

      private const byte MessageHeaderPosition = 0;
      private const byte MessageHeaderLength = 1;

      // nRF24 Hardware interface configuration
      private const byte RF24ModuleChipEnablePin = 25;
      private const byte RF24ModuleChipSelectPin = 0;
      private const byte RF24ModuleInterruptPin = 17;

      private LoggingChannel logging = new LoggingChannel("devMobile Azure IotHub nRF24L01 Field Gateway", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
      private ApplicationSettings applicationSettings = null;
      private RF24 rf24 = new RF24();
      private DeviceClient azureIoTHubClient = null;
      private BackgroundTaskDeferral deferral;

      private enum MessagePayloadType : byte
      {
         Echo = 0,
         DeviceIdPlusCsvSensorReadings,
      }

      public void Run(IBackgroundTaskInstance taskInstance)
      {
         if (!this.ConfigurationFileLoad().Result)
         {
            return;
         }

         // Connect the IoT hub first so we are ready for any messages
         LoggingFields azureIoTHubSettings = new LoggingFields();
         azureIoTHubSettings.AddString("DeviceConnectionString", this.applicationSettings.AzureIoTHubDeviceConnectionString);
         azureIoTHubSettings.AddString("TransportType", this.applicationSettings.AzureIoTHubTransportType.ToString());
         azureIoTHubSettings.AddString("SensorIDIsDeviceIDSensorID", this.applicationSettings.SensorIDIsDeviceIDSensorID.ToString());
         this.logging.LogEvent("AzureIoTHub configuration", azureIoTHubSettings, LoggingLevel.Information);

         try
         {
            this.azureIoTHubClient = DeviceClient.CreateFromConnectionString(this.applicationSettings.AzureIoTHubDeviceConnectionString, this.applicationSettings.AzureIoTHubTransportType);
         }
         catch (Exception ex)
         {
            this.logging.LogMessage("IoT Hub connection failed " + ex.Message, LoggingLevel.Error);
            return;
         }

         // Configure the nRF24L01 module
         this.rf24.OnDataReceived += this.Radio_OnDataReceived;
         this.rf24.OnTransmitFailed += this.Radio_OnTransmitFailed;
         this.rf24.OnTransmitSuccess += this.Radio_OnTransmitSuccess;

         this.rf24.Initialize(RF24ModuleChipEnablePin, RF24ModuleChipSelectPin, RF24ModuleInterruptPin);
         this.rf24.Address = Encoding.UTF8.GetBytes(this.applicationSettings.RF24Address);
         this.rf24.Channel = this.applicationSettings.RF24Channel;

         // The order of setting the power level and Data rate appears to be important, most probably register masking issue in NRF24 library which needs some investigation
         this.rf24.PowerLevel = this.applicationSettings.RF24PowerLevel;
         this.rf24.DataRate = this.applicationSettings.RF24DataRate;
         this.rf24.IsAutoAcknowledge = this.applicationSettings.IsRF24AutoAcknowledge;
         this.rf24.IsDyanmicAcknowledge = this.applicationSettings.IsRF24DynamicAcknowledge;
         this.rf24.IsDynamicPayload = this.applicationSettings.IsRF24DynamicPayload;
         this.rf24.IsEnabled = true;

         LoggingFields rf24Settings = new LoggingFields();
         rf24Settings.AddUInt8("Channel", this.applicationSettings.RF24Channel);
         rf24Settings.AddString("Address", this.applicationSettings.RF24Address);
         rf24Settings.AddString("DataRate", this.applicationSettings.RF24DataRate.ToString());
         rf24Settings.AddString("PowerLevel", this.applicationSettings.RF24PowerLevel.ToString());
         rf24Settings.AddBoolean("AutoAcknowledge", this.applicationSettings.IsRF24AutoAcknowledge);
         rf24Settings.AddBoolean("DynamicAcknowledge", this.applicationSettings.IsRF24DynamicAcknowledge);
         rf24Settings.AddBoolean("DynamicPayload", this.applicationSettings.IsRF24DynamicPayload);
         this.logging.LogEvent("nRF24L01 configuration", rf24Settings, LoggingLevel.Information);

         this.deferral = taskInstance.GetDeferral();
      }

      private async Task<bool> ConfigurationFileLoad()
      {
         StorageFolder localFolder = ApplicationData.Current.LocalFolder;

         try
         {
            // Check to see if file exists
            if (localFolder.TryGetItemAsync(ConfigurationFilename).GetAwaiter().GetResult() == null)
            {
               this.logging.LogMessage("Configuration file " + ConfigurationFilename + " not found", LoggingLevel.Error);

               this.applicationSettings = new ApplicationSettings()
               {
                  AzureIoTHubDeviceConnectionString = "Azure IoT Hub connection string goes here",
                  AzureIoTHubTransportType = TransportType.Amqp,
                  RF24Address = "Base1",
                  SensorIDIsDeviceIDSensorID = false,
                  RF24Channel = 10,
                  RF24DataRate = DataRate.DR250Kbps,
                  RF24PowerLevel = PowerLevel.High,
                  IsRF24AutoAcknowledge = true,
                  IsRF24DynamicAcknowledge = false,
                  IsRF24DynamicPayload = true,
               };

               // Create empty configuration file
               StorageFile configurationFile = await localFolder.CreateFileAsync(ConfigurationFilename, CreationCollisionOption.OpenIfExists);
               using (Stream stream = await configurationFile.OpenStreamForWriteAsync())
               {
                  using (TextWriter streamWriter = new StreamWriter(stream))
                  {
                     streamWriter.Write(JsonConvert.SerializeObject(this.applicationSettings, Formatting.Indented));
                  }
               }

               return false;
            }
            else
            {
               // Load the configuration settings
               StorageFile configurationFile = await localFolder.CreateFileAsync(ConfigurationFilename, CreationCollisionOption.OpenIfExists);
               using (Stream stream = await configurationFile.OpenStreamForReadAsync())
               {
                  using (TextReader streamReader = new StreamReader(stream))
                  {
                     this.applicationSettings = JsonConvert.DeserializeObject<ApplicationSettings>(streamReader.ReadToEnd());
                  }
               }

               return true;
            }
         }
         catch (Exception ex)
         {
            this.logging.LogMessage("Configuration file " + ConfigurationFilename + " load failed " + ex.Message, LoggingLevel.Error);
            return false;
         }
      }

      private void Radio_OnDataReceived(byte[] messageData)
      {
         // Check the payload is long enough to contain header length
         if (messageData.Length < MessageHeaderLength)
         {
            this.logging.LogMessage("Message too short for header", LoggingLevel.Warning);
            return;
         }

         // First nibble is the message type
         switch ((MessagePayloadType)(messageData[MessageHeaderPosition] >> 4))
         {
            case MessagePayloadType.Echo:
               this.MessageDataDisplay(messageData);
               break;
            case MessagePayloadType.DeviceIdPlusCsvSensorReadings:
               this.MessageDataSensorDeviceIdPlusCsvData(messageData);
               break;
            default:
               this.MessageDataDisplay(messageData);
               break;
         }
      }

      private void Radio_OnTransmitSuccess()
      {
         this.logging.LogMessage("Transmit Succeeded");
         Debug.WriteLine("Transmit Succeeded!");
      }

      private void Radio_OnTransmitFailed()
      {
         this.logging.LogMessage("Transmit failed");
         Debug.WriteLine("Transmit Failed!");
      }

      private void MessageDataDisplay(byte[] messageData)
      {
         string bcdText = BitConverter.ToString(messageData);
         string unicodeText = Encoding.UTF8.GetString(messageData);

         Debug.WriteLine("BCD - Length {0} Payload {1}", messageData.Length, bcdText);
         Debug.WriteLine("Unicode - Length {0} Payload {1}", messageData.Length, unicodeText);

         LoggingFields messagePayload = new LoggingFields();
         messagePayload.AddString("BCD", bcdText);
         messagePayload.AddString("Unicode", unicodeText);
         this.logging.LogEvent("Message Data", messagePayload, LoggingLevel.Verbose);
      }

      private async void MessageDataSensorDeviceIdPlusCsvData(byte[] messageData)
      {
         char[] sensorReadingSeparators = new char[] { ',' };
         char[] sensorIdAndValueSeparators = new char[] { ' ' };

         byte deviceIdLength = (byte)(messageData[MessageHeaderPosition] & (byte)0b1111);

         // Check the payload is long enough to contain the header & specified SensorDeviceID length
         if (messageData.Length < MessageHeaderLength + deviceIdLength)
         {
            this.logging.LogMessage("Message data too short to contain device identifier", LoggingLevel.Warning);
            return;
         }

         string deviceId = BitConverter.ToString(messageData, MessageHeaderLength, deviceIdLength);

         // Check that there is a payload
         if (messageData.Length <= MessageHeaderLength + deviceIdLength)
         {
            this.logging.LogMessage("Message data too short to contain any sensor readings", LoggingLevel.Warning);
            return;
         }

         // Copy the payload access to string where it can be chopped up
         string payload = Encoding.UTF8.GetString(messageData, MessageHeaderLength + deviceIdLength, messageData.Length - (MessageHeaderLength + deviceIdLength));

         Debug.WriteLine("{0:hh:mm:ss} DeviceID {1} Length {2} Payload {3} Length {4}", DateTime.UtcNow, deviceId, deviceIdLength, payload, payload.Length);

         // Chop up the CSV text
         string[] sensorReadings = payload.Split(sensorReadingSeparators, StringSplitOptions.RemoveEmptyEntries);
         if (sensorReadings.Length < 1)
         {
            this.logging.LogMessage("Payload contains no sensor readings", LoggingLevel.Warning);
            return;
         }

         JObject telemetryDataPoint = new JObject(); // This could be simplified but for field gateway will use this style
         LoggingFields sensorData = new LoggingFields();

         telemetryDataPoint.Add("DeviceID", deviceId);
         sensorData.AddString("DeviceID", deviceId);

         // Chop up each sensor read into an ID & value
         foreach (string sensorReading in sensorReadings)
         {
            string[] sensorIdAndValue = sensorReading.Split(sensorIdAndValueSeparators, StringSplitOptions.RemoveEmptyEntries);

            // Check that there is an id & value
            if (sensorIdAndValue.Length != 2)
            {
               this.logging.LogMessage("Sensor reading invalid format", LoggingLevel.Warning);
               return;
            }

            string sensorId = sensorIdAndValue[0];
            string value = sensorIdAndValue[1];

            if (this.applicationSettings.SensorIDIsDeviceIDSensorID)
            {
               // Construct the sensor ID from SensordeviceID & Value ID
               telemetryDataPoint.Add(string.Format("{0}{1}", deviceId, sensorId), value);

               sensorData.AddString(string.Format("{0}{1}", deviceId, sensorId), value);
               Debug.WriteLine(" Sensor {0}{1} Value {2}", deviceId, sensorId, value);
            }
            else
            {
               telemetryDataPoint.Add(sensorId, value);
               Debug.WriteLine(" Device {0} Sensor {1} Value {2}", deviceId, sensorId, value);
            }
         }

         this.logging.LogEvent("Sensor readings", sensorData, LoggingLevel.Information);

         try
         {
            using (Message message = new Message(Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(telemetryDataPoint))))
            {
               Debug.WriteLine(" AzureIoTHubClient SendEventAsync start");
               await this.azureIoTHubClient.SendEventAsync(message);
               Debug.WriteLine(" AzureIoTHubClient SendEventAsync finish");
            }
         }
         catch (Exception ex)
         {
            this.logging.LogMessage("AzureIoTHubClient SendEventAsync failed " + ex.Message, LoggingLevel.Error);
         }
      }

      private class ApplicationSettings
      {
         [JsonProperty("AzureIoTHubDeviceConnectionString", Required = Required.Always)]
         public string AzureIoTHubDeviceConnectionString { get; set; }

         [JsonProperty("AzureIoTHubTransportType", Required = Required.Always)]
         [JsonConverter(typeof(StringEnumConverter))]
         public TransportType AzureIoTHubTransportType { get; internal set; }

         [JsonProperty("SensorIDIsDeviceIDSensorID", Required=Required.Always)]
         public bool SensorIDIsDeviceIDSensorID { get; set; }

         [JsonProperty("RF24Address", Required = Required.Always)]
         public string RF24Address { get; set; }

         [JsonProperty("RF24Channel", Required = Required.Always)]
         public byte RF24Channel { get; set; }

         [JsonProperty("RF24DataRate", Required = Required.Always)]
         [JsonConverter(typeof(StringEnumConverter))]
         public DataRate RF24DataRate { get; set; }

         [JsonProperty("RF24PowerLevel", Required = Required.Always)]
         [JsonConverter(typeof(StringEnumConverter))]
         public PowerLevel RF24PowerLevel { get; set; }

         [JsonProperty("RF24AutoAcknowledge", Required = Required.Always)]
         public bool IsRF24AutoAcknowledge { get; set; }

         [JsonProperty("RF24DynamicAcknowledge", Required = Required.Always)]
         public bool IsRF24DynamicAcknowledge { get; set; }

         [JsonProperty("RF24DynamicPayload", Required = Required.Always)]
         public bool IsRF24DynamicPayload { get; set; }
      }
   }
}
