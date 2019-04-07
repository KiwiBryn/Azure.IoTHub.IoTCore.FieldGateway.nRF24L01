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
#if CLOUD2DEVICE_SEND
	using System.Collections.Concurrent;
#endif
	using System.Diagnostics;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Text;
   using System.Threading.Tasks;
   using Microsoft.Azure.Devices.Client;
   using Newtonsoft.Json;
   using Newtonsoft.Json.Converters;
   using Newtonsoft.Json.Linq;
   using Radios.RF24;
	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
   using Windows.Foundation.Diagnostics;
   using Windows.Storage;
	using Windows.System;

	public sealed class StartupTask : IBackgroundTask
   {
      private const string ConfigurationFilename = "config.json";

      private const byte MessageHeaderPosition = 0;
      private const byte MessageHeaderLength = 1;
		private const byte MessageAddressLengthMinimum = 3;
		private const byte MessageAddressLengthMaximum = 5;
		private const byte MessagePayloadLengthMinimum = 0;
		private const byte MessagePayloadLengthMaximum = 24;

		// nRF24 Hardware interface configuration
#if CEECH_NRF24L01P_SHIELD
		private const byte RF24ModuleChipEnablePin = 25;
      private const byte RF24ModuleChipSelectPin = 0;
      private const byte RF24ModuleInterruptPin = 17;
#endif

#if BOROS_RF2_SHIELD_RADIO_0
		private const byte RF24ModuleChipEnablePin = 24;
      private const byte RF24ModuleChipSelectPin = 0;
      private const byte RF24ModuleInterruptPin = 27;
#endif

#if BOROS_RF2_SHIELD_RADIO_1
		private const byte RF24ModuleChipEnablePin = 25;
      private const byte RF24ModuleChipSelectPin = 1;
      private const byte RF24ModuleInterruptPin = 22;
#endif

		private readonly LoggingChannel logging = new LoggingChannel("devMobile Azure IotHub nRF24L01 Field Gateway", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
      private readonly RF24 rf24 = new RF24();
		private readonly TimeSpan deviceRebootDelayPeriod = new TimeSpan(0, 0, 25);
		private ApplicationSettings applicationSettings = null;
      private DeviceClient azureIoTHubClient = null;
#if CLOUD2DEVICE_SEND
		private ConcurrentDictionary<byte[], byte[]> sendMessageQueue = new ConcurrentDictionary<byte[], byte[]>();
#endif
		private BackgroundTaskDeferral deferral;

      private enum MessagePayloadType : byte
      {
         Echo = 0,
         DeviceIdPlusCsvSensorReadings,
			DeviceIdPlusBinaryPayload,
      }

      public void Run(IBackgroundTaskInstance taskInstance)
      {
         if (!this.ConfigurationFileLoad().Result)
         {
            return;
         }

			// Log the Application build, shield information etc.
			LoggingFields applicationBuildInformation = new LoggingFields();
#if CEECH_NRF24L01P_SHIELD
			applicationBuildInformation.AddString("Shield", "CeechnRF24L01P");
#endif
#if BOROS_RF2_SHIELD_RADIO_0
			appllcationBuildInformation.AddString("Shield", "BorosRF2Port0");
#endif
#if BOROS_RF2_SHIELD_RADIO_1
			applicationBuildInformation.AddString("Shield", "BorosRF2Port1");
#endif
#if CLOUD_DEVICE_BOND
			applicationBuildInformation.AddString("Bond", "Supported");
#else
			applicationBuildInformation.AddString("Bond", "NotSupported");
#endif
#if CLOUD_DEVICE_PUSH
			applicationBuildInformation.AddString("Push", "Supported");
#else
			applicationBuildInformation.AddString("Push", "NotSupported");
#endif
#if CLOUD_DEVICE_SEND
			applicationBuildInformation.AddString("Send", "Supported");
#else
			applicationBuildInformation.AddString("Send", "NotSupported");
#endif
			applicationBuildInformation.AddString("Timezone", TimeZoneSettings.CurrentTimeZoneDisplayName);
			applicationBuildInformation.AddString("OSVersion", Environment.OSVersion.VersionString);
			applicationBuildInformation.AddString("MachineName", Environment.MachineName);

			// This is from the application manifest
			Package package = Package.Current;
			PackageId packageId = package.Id;
			PackageVersion version = packageId.Version;

			applicationBuildInformation.AddString("ApplicationVersion", string.Format($"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"));
			this.logging.LogEvent("Application starting", applicationBuildInformation, LoggingLevel.Information);

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

			// Wire up the field gateway restart method handler
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("Restart", this.RestartAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client Restart method handler setup failed " + ex.Message, LoggingLevel.Error);
				return;
			}

#if CLOUD_DEVICE_BOND
			// Wire up the bond device method handler
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("DeviceBond", this.DeviceBondAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub Device Bond method handler setup failed " + ex.Message, LoggingLevel.Error);
				return;
			}
#endif

#if CLOUD_DEVICE_PUSH
			// Wire up the push message to device method handler
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("DevicePush", this.DevicePushAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client DevicePush SetMethodHandlerAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
#endif

#if CLOUD_DEVICE_SEND
			// Wire up the send message to device method handler
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("DeviceSend", this.DeviceSendAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client DeviceSend SetMethodHandlerAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
#endif

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

            try
            {
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

                  sensorData.AddString(sensorId, value);
                  Debug.WriteLine(" Device {0} Sensor {1} Value {2}", deviceId, sensorId, value);
               }
            }
            catch (Exception ex)
            {
               this.logging.LogMessage("Sensor reading invalid JSON format " + ex.Message, LoggingLevel.Warning);
               return;
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

		private async Task<MethodResponse> RestartAsync(MethodRequest methodRequest, object userContext)
		{
			this.logging.LogEvent("Reboot initiated");

			ShutdownManager.BeginShutdown(ShutdownKind.Restart, this.deviceRebootDelayPeriod);

			return new MethodResponse(200);
		}

#if CLOUD_DEVICE_BOND
		private async Task<MethodResponse> DeviceBondAsync(MethodRequest methodRequest, object userContext)
		{
			LoggingFields bondLoggingInfo = new LoggingFields();

			try
			{
				dynamic json = JValue.Parse(methodRequest.DataAsJson);

				string deviceAddressBcd = json.DeviceAddress;
				bondLoggingInfo.AddString("DeviceAddressBCD", deviceAddressBcd);
				Debug.WriteLine($"DeviceBondAsync DeviceAddressBCD {deviceAddressBcd}");

				byte[] deviceAddressBytes = deviceAddressBcd.Split('-').Select(x => byte.Parse(x, NumberStyles.HexNumber)).ToArray();
				bondLoggingInfo.AddInt32("DeviceAddressBytes Length", deviceAddressBytes.Length);
				Debug.WriteLine($"DeviceBondAsync DeviceAddressLength {deviceAddressBytes.Length}");

				if ((deviceAddressBytes.Length < .AddressLengthMinimum) || (deviceAddressBytes.Length > .AddressLengthMaximum))
				{
					this.logging.LogEvent("DeviceBondAsync failed device address bytes length", bondLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(414);
				}

				// Empty payload for bond message
				byte[] payloadBytes = { };

				rf24.Send(deviceAddressBytes, payloadBytes);

				this.logging.LogEvent("DeviceBondAsync success", bondLoggingInfo, LoggingLevel.Information);
			}
			catch (Exception ex)
			{
				bondLoggingInfo.AddString("Exception", ex.ToString());
				this.logging.LogEvent("DeviceBondAsync exception", bondLoggingInfo, LoggingLevel.Error);
				return new MethodResponse(400);
			}

			return new MethodResponse(200);
		}
#endif

#if CLOUD2DEVICE_SEND
		private async Task<MethodResponse> DeviceSendAsync(MethodRequest methodRequest, object userContext)
		{
			LoggingFields sendLoggingInfo = new LoggingFields();

			this.logging.LogEvent("Send BCD initiated");

			try
			{
				// Initially use a dynamic maybe use a decorated class in future
				dynamic json = JValue.Parse(methodRequest.DataAsJson);

				string deviceAddressBcd = json.DeviceAddress;
				sendLoggingInfo.AddString("DeviceAddressBCD", deviceAddressBcd);
				Debug.WriteLine($"DeviceSendAsync DeviceAddressBCD {deviceAddressBcd}");

				byte[] deviceAddressBytes = deviceAddressBcd.Split('-').Select(x => byte.Parse(x, NumberStyles.HexNumber)).ToArray();
				sendLoggingInfo.AddInt32("DeviceAddressBytes Length", deviceAddressBytes.Length);
				Debug.WriteLine($"DeviceSendAsync DeviceAddressLength {deviceAddressBytes.Length}");

				if ((deviceAddressBytes.Length < .AddressLengthMinimum) || (deviceAddressBytes.Length > .AddressLengthMaximum))
				{
					this.logging.LogEvent("DeviceSendAsync failed device address bytes length", sendLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(414);
				}

				string messagedBcd = json.DevicePayload;
				sendLoggingInfo.AddString("MessageBCD", messagedBcd);

				byte[] messageBytes = messagedBcd.Split('-').Select(x => byte.Parse(x, NumberStyles.HexNumber)).ToArray(); // changed the '-' to ' '
				sendLoggingInfo.AddInt32("MessageBytesLength", messageBytes.Length);
				Debug.WriteLine($"DeviceSendAsync DeviceAddress:{deviceAddressBcd} Payload:{messagedBcd}");

				if ((messageBytes.Length < .MessageLengthMinimum) || (messageBytes.Length > .MessageLengthMaximum))
				{
					this.logging.LogEvent("DeviceSendAsync failed payload Length", sendLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(413);
				}

				if (sendMessageQueue.TryAdd(deviceAddressBytes, messageBytes))
				{
					this.logging.LogEvent("DeviceSendAsync failed message already queued", sendLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(409);
				}

				this.logging.LogEvent("DeviceSendAsync success", sendLoggingInfo, LoggingLevel.Information);
			}
			catch (Exception ex)
			{
				sendLoggingInfo.AddString("Exception", ex.ToString());
				this.logging.LogEvent("DeviceSendAsync failed exception", sendLoggingInfo, LoggingLevel.Error);
				return new MethodResponse(400);
			}

			return new MethodResponse(200);
		}
#endif

#if CLOUD2DEVICE_PUSH
		private async Task<MethodResponse> DevicePushAsync(MethodRequest methodRequest, object userContext)
		{
			LoggingFields pushLoggingInfo = new LoggingFields();

			this.logging.LogEvent("Push BCD initiated");

			try
			{
				// Initially use a dynamac maybe use a decorated class in future +flexibility -performance
				dynamic json = JValue.Parse(methodRequest.DataAsJson);

				// Prepare the server address bytes for the message header and validate length
				byte[] serverAddressBytes = Encoding.UTF8.GetBytes(this.applicationSettings.RF24Address);
				pushLoggingInfo.AddInt32("serverAddressBytes Length", serverAddressBytes.Length);

				if (serverAddressBytes.Length > MessageAddressLengthMaximum)
				{
					this.logging.LogEvent("DevicePushBcdAsync failed server address bytes Length", pushLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(400);
				}

				// Convert the device address from the JSON payload to bytes and validate length
				string deviceAddressBcd = json.DeviceAddress;
				pushLoggingInfo.AddString("DeviceAddressBCD", deviceAddressBcd);

				byte[] deviceAddressBytes = deviceAddressBcd.Split('-').Select(x => byte.Parse(x, NumberStyles.HexNumber)).ToArray();
				pushLoggingInfo.AddInt32("DeviceAddressBytes Length", deviceAddressBytes.Length);

				if ((deviceAddressBytes.Length < ) || (deviceAddressBytes.Length > ))
				{
					this.logging.LogEvent("DevicePushAsync failed device address bytes length", pushLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(414);
				}

				string messagedBcd = json.DevicePayload;
				pushLoggingInfo.AddString("MessageBCD", messagedBcd);

				byte[] messageBytes = messagedBcd.Split('-').Select(x => byte.Parse(x, NumberStyles.HexNumber)).ToArray();
				pushLoggingInfo.AddInt32("MessageBytesLength", messageBytes.Length);

				Debug.WriteLine($"BondDeviceAsync DeviceAddress:{deviceAddressBcd} Payload:{messagedBcd} ServerAddress:{serverAddressBytes}");

				int payloadLength = MessageHeaderLength + deviceAddressBytes.Length + messageBytes.Length;
				pushLoggingInfo.AddInt32("PayloadLength", payloadLength);

				if (payloadLength < MessagePayloadLengthMinimum || (payloadLength > MessagePayloadLengthMaximum))
				{
					this.logging.LogEvent("DevicePushBcdAsync failed payload Length", pushLoggingInfo, LoggingLevel.Error);
					return new MethodResponse(400);
				}

				// Assemble payload to send to device
				byte[] payloadBytes = new byte[payloadLength];

				payloadBytes[0] = (byte)(((byte)MessagePayloadType.DeviceIdPlusBinaryPayload << 4) | deviceAddressBytes.Length);
				Array.Copy(serverAddressBytes, 0, payloadBytes, MessageHeaderLength, serverAddressBytes.Length);
				Array.Copy(messageBytes, 0, payloadBytes, MessageHeaderLength + serverAddressBytes.Length, messageBytes.Length);

				this.rf24.SendTo(deviceAddressBytes, payloadBytes);

				this.logging.LogEvent("Device Push BCD data", pushLoggingInfo, LoggingLevel.Information);
			}
			catch (Exception ex)
			{
				pushLoggingInfo.AddString("Exception", ex.ToString());
				this.logging.LogEvent("DevicePushAsync failed exception", pushLoggingInfo, LoggingLevel.Error);

				return new MethodResponse(400);
			}

			return new MethodResponse(200);
		}
#endif

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
