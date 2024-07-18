
using Newtonsoft.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using VagabondK.Protocols.Channels;
using VagabondK.Protocols.LSElectric;
using VagabondK.Protocols.LSElectric.FEnet;

namespace EdukitConnector
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var instance = new Service();
            instance.Start();
        }

        public class Service
        {
            private static FEnetClient fenetClient;
            public static MqttClient mqttClient;
            static EdukitConfig EdukitConfigResult = null;
            static List<XGTAddressData> devicesList = null;
            private static Dictionary<string, string> SWITCH_MEMORY = new Dictionary<string, string>
            {
                { "%MX111", "0" },
                { "%MX127", "0" },
                { "%MX143", "0" },
                { "%MX159", "0" },
                { "%MX174", "0" },
            };

            internal async void Start()
            {
                SetConfig();
                await Connect();
            }

            private static void SetConfig()
            {
                string fullpathFile = AppDomain.CurrentDomain.BaseDirectory;
                string EdukitConfigFile = fullpathFile + "//EdukitConfigFile.json";

                string EdukitConfig = File.ReadAllText(EdukitConfigFile);
                EdukitConfigResult = JsonConvert.DeserializeObject<EdukitConfig>(EdukitConfig);

                string deviceFile = fullpathFile + "//Devices.json";
                string devices = File.ReadAllText(deviceFile);

                devicesList = JsonConvert.DeserializeObject<List<XGTAddressData>>(devices);
            }

            public async Task<Boolean> Connect()
            {
                try
                {
                    var ip = EdukitConfigResult.EdukitIP;
                    var port = Int32.Parse(EdukitConfigResult.EdukitPort);
                    int DelayTime = Int32.Parse(EdukitConfigResult.DelayTime);
                    int mqttport = Int32.Parse(EdukitConfigResult.MqttBrokerPort);

                    mqttClient = null;
                    if (EdukitConfigResult.MqttCert == null)
                    {
                        mqttClient = new MqttClient(EdukitConfigResult.MqttBrokerIP, mqttport, false, null, null, MqttSslProtocols.TLSv1_2);
                    }
                    else
                    {
                        X509Certificate clientCert = new X509Certificate2(EdukitConfigResult.MqttCert, EdukitConfigResult.MqttPw);
                        X509Certificate caCert = new X509Certificate(EdukitConfigResult.MqttCa);
                        mqttClient = new MqttClient(EdukitConfigResult.MqttBrokerIP, mqttport, true, caCert, clientCert, MqttSslProtocols.TLSv1_2);
                    }
                    mqttClient.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;

                    byte code = mqttClient.Connect(Guid.NewGuid().ToString());

                    mqttClient.MqttMsgPublishReceived += (sender, e) => client_MqttMsgPublishReceived(sender, e, fenetClient);

                    mqttClient.ConnectionClosed += (sender, e) => Console.WriteLine("연결이 닫혔습니다.");
                    mqttClient.MqttMsgSubscribed += (sender, e) => Console.WriteLine($"구독 성공: {e.MessageId}");
                    mqttClient.MqttMsgUnsubscribed += (sender, e) => Console.WriteLine($"구독 해제: {e.MessageId}");

                    ushort messageIds = mqttClient.Subscribe(new string[] { "edge/edukit/control" }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                    Console.WriteLine($"구독 메시지 ID: {messageIds}");

                    Console.WriteLine($"Connection result: {code}");

                    Console.WriteLine("-------------- Edukit Information --------------");
                    Console.WriteLine("Edukit Connection State : True");
                    Console.WriteLine("Edukit IP : " + ip);
                    Console.WriteLine("Edukit PORT : " + port + "\n");
                    Console.WriteLine("--------------  MQTT Information  --------------");
                    Console.WriteLine("MQTT Connection State : True");
                    Console.WriteLine("MQTT IP : " + EdukitConfigResult.MqttBrokerIP);
                    Console.WriteLine("MQTT Broker Port : " + EdukitConfigResult.MqttBrokerPort);
                    Console.WriteLine("delaytime : " + EdukitConfigResult.DelayTime);

                    XGTClass xGTClass = new XGTClass(ip, port);

                    ConnectionStart(DelayTime, xGTClass, ip, port);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message + "\n" + ex.StackTrace);
                }
                return true;
            }

            private void ConnectionStart(int DelayTime, XGTClass xGTClass, string ip, int port)
            {
                //List<EdukitNewdata> edukitData = new List<EdukitNewdata>();

                XGTData val = null;

                fenetClient = new FEnetClient(new TcpChannel(ip, port))
                {
                    // hex bit use
                    UseHexBitIndex = true,

                    //`` NAK 응답 예외 발생 X 처리
                    ThrowsExceptionFromNAK = false
                };

                xGTClass.Connect(ip, port);

                var list = new List<DeviceVariable>();

                // LS PLC 디바이스 데이터 수집
                foreach (var address in devicesList)
                {
                    if (!Enum.TryParse(address.MemoryArea, out LS_DEVICE_TYPE AreaEnum) || !Enum.TryParse(AreaEnum.ToString(), out DeviceType PlcArea)) { throw new Exception(); }

                    if (address.MemoryType == "Bit") { list.Add(new DeviceVariable(PlcArea, DataType.Bit, ConvertAddressNumber(address.Address))); }

                    else if (address.MemoryType == "Word" || address.MemoryArea == "K") { list.Add(new DeviceVariable(PlcArea, DataType.Word, Convert.ToUInt32(address.Address))); }

                }
                var subLists = list.Select((x, i) => new { Index = i, Value = x }).GroupBy(x => x.Index / 16).Select(x => x.Select(v => v.Value).ToList()).ToList();

                while (true)
                {
                    try
                    {
                        List<EdukitNewdata> edukitData = new List<EdukitNewdata>();

                        Dictionary<XGTAddressData, ReceivedReadData> DataTagList = new Dictionary<XGTAddressData, ReceivedReadData>();

                        var readDataList = new List<ReceivedReadData>();

                        foreach (var subList in subLists)
                        {
                            var result = fenetClient.Read(subList);

                            foreach (var data in result)
                            {
                                ReceivedReadData readData = new ReceivedReadData
                                {
                                    memoryBunji = data.Key.DeviceType + data.Key.Index.ToString(),
                                    deviceType = data.Key.DeviceType.ToString(),
                                    dataType = data.Key.DataType.ToString(),
                                    bitValue = data.Value.BitValue.ToString(),
                                    wordValue = data.Value.LongWordValue.ToString()
                                };

                                if (string.Equals(readData.memoryBunji, "D1101")) readData.wordValue = (data.Value.WordValue / 10).ToString();

                                //K device DWord
                                if (string.Equals(readData.deviceType, "K") && string.Equals(readData.dataType, "Word"))
                                {
                                    string DwordAddress = "%" + data.Key.DeviceType + "W" + (data.Key.Index + 1).ToString();
                                    var dwordValue = fenetClient.Read(DwordAddress);
                                    foreach (var address in dwordValue)
                                    {
                                        int wordValueInt = int.Parse(data.Value.LongWordValue.ToString()) + (int.Parse(address.Value.LongWordValue.ToString()) * 65535);
                                        readData.wordValue = wordValueInt.ToString();
                                    }
                                }

                                string memoryDicKey = $"%MX{readData.memoryBunji.Substring(1)}";
                                if (SWITCH_MEMORY.ContainsKey(memoryDicKey))
                                {
                                    Console.WriteLine($"bit: {readData.wordValue}");
                                    SWITCH_MEMORY[memoryDicKey] = readData.wordValue;
                                }
                                readDataList.Add(readData);
                            }
                        }

                        //
                        for (var index = 0; index < devicesList.Count; index++)
                        {
                            XGTAddressData tagData = new XGTAddressData
                            {
                                Address = devicesList[index].Address,
                                Name = devicesList[index].Name,
                                TagId = devicesList[index].TagId
                            };
                            DataTagList.Add(tagData, readDataList[index]);
                        }
                        List<EdukitNewdata> Data = new List<EdukitNewdata>();

                        //DataTagList -> edukitData
                        foreach (var data in DataTagList)
                        {
                            EdukitNewdata newdata = new EdukitNewdata
                            {
                                name = data.Key.Name,
                                tagId = data.Key.TagId,
                                value = data.Value
                            };
                            if (data.Value.dataType == "Bit") { newdata.value = data.Value.bitValue == "True" ? true : false; }
                            else if (data.Value.dataType == "Word") { newdata.value = data.Value.wordValue; }

                            edukitData.Add(newdata);
                        }

                        //timestamp
                        EdukitNewdata timeData = new EdukitNewdata
                        {
                            name = "DataTime",
                            tagId = "0",
                            value = DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'")
                        };
                        edukitData.Add(timeData);

                        MqttData(edukitData, "edge/edukit/status");

                        if (EdukitConfigResult.DebugType == "Debug")
                        {
                            //Console.Clear();
                            //List<EdukitNewdata> SortedList = edukitData.OrderBy(x => Int64.Parse(x.tagId)).ToList();

                            foreach (var data in edukitData)
                            {
                                Console.WriteLine($"[{data.tagId}]{data.name} : {data.value}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ResponseTimeout, Reconnecting...", ex.Message);
                        Thread.Sleep(10000);
                    }
                    Thread.Sleep(DelayTime);
                }
            }

            static void MqttData(List<EdukitNewdata> EduKitData, string topic)
            {
                try
                {
                    //MQTT Publish
                    mqttClient.Publish(topic, Encoding.Default.GetBytes(JsonConvert.SerializeObject(EduKitData)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message + "\n" + ex.StackTrace);
                }
            }

            static void client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e, FEnetClient fenetClient)
            {
                string message = System.Text.Encoding.UTF8.GetString(e.Message);
                Console.WriteLine(sender);
                Console.WriteLine(message);

                try
                {
                    // JSON 문자열을 DeviceMessage 객체로 변환
                    MemoryMessage memoryMessage = JsonConvert.DeserializeObject<MemoryMessage>(message);

                    // 객체의 속성을 출력
                    Console.WriteLine($"Received message from topic {e.Topic}:");
                    Console.WriteLine($"TagId: {memoryMessage.tagId}, Value: {memoryMessage.value}");

                    try
                    {
                        // plc에 데이터 쓰기
                        string memoryAddress = LS_MEMORY[memoryMessage.tagId];
                        Console.WriteLine($"Memory: {memoryAddress}");
                        int intValue = int.Parse(memoryMessage.value);
                        if (SWITCH_MEMORY.ContainsKey(memoryAddress))
                        {
                            if (intValue == 0)
                            {
                                return;
                            }
                            intValue = int.Parse(SWITCH_MEMORY[memoryAddress]) ^ 1;
                            SWITCH_MEMORY[memoryAddress] = intValue.ToString();
                        }
                        fenetClient.Write(memoryAddress, intValue);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
                catch (JsonException ex)
                {
                    // JSON 파싱 오류 처리
                    Console.WriteLine($"Failed to parse JSON message: {ex.Message}");
                }
            }

            static uint ConvertAddressNumber(string input)
            {
                if (string.IsNullOrEmpty(input))
                {
                    throw new ArgumentException("Input cannot be null or empty.");
                }

                // 마지막 문자는 16진수로 처리
                char lastChar = input[^1];
                uint lastDigit = Convert.ToUInt32(lastChar.ToString(), 16);

                if (input.Length == 1)
                {
                    // 문자열이 한 자리일 경우, 그 값만 반환
                    return lastDigit;
                }

                // 나머지 앞 부분은 10진수로 처리
                string prefix = input.Substring(0, input.Length - 1);
                uint prefixNumber = Convert.ToUInt32(prefix, 10);

                // 결과 계산: 10진수 부분 * 16 + 16진수 부분
                return prefixNumber * 16 + lastDigit;
            }
        }

        public class EdukitNewdata
        {
            public string tagId { get; set; }
            public string name { get; set; }
            public object value { get; set; }
        }

        public class EdukitConfig
        {
            public string EdukitId { get; set; }
            public string EdukitIP { get; set; }
            public string EdukitPort { get; set; }
            public string MqttBrokerIP { get; set; }
            public string MqttBrokerPort { get; set; }
            public string MqttCert { get; set; }
            public string MqttPw { get; set; }
            public string MqttCa { get; set; }
            public string WebSocketServerUrl { get; set; }
            public string DelayTime { get; set; }
            public string DebugType { get; set; }
        }

        public class ReceivedReadData
        {
            public string deviceType { get; set; }
            public string dataType { get; set; }
            public string bitValue { get; set; }
            public string wordValue { get; set; }
            public string memoryBunji { get; set; }
        }

        public class ReceivedData
        {
            public string tagId { get; set; }
            public string value { get; set; }
        }

        public enum LS_DEVICE_TYPE
        {
            Unknown = 0,
            P,
            M,
            K,
            F,
            T,
            C,
            L,
            D,
            S,
            Z,
            N,
            R,
            U
        }

        private static readonly Dictionary<string, string> LS_MEMORY = new Dictionary<string, string>
        {
            { "1", "%MX33" },
            { "2", "%MX34" },
            { "3", "%MX15" },
            { "4", "%MX111" },
            { "5", "%MX127" },
            { "6", "%MX143" },
            { "7", "%MX159" },
            { "8", "%MX174" },
        };

        public class MemoryMessage
        {
            public string tagId { get; set; }
            public string name { get; set; }
            public string value { get; set; }
        }
    }
}
