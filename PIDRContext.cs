using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons
{
	internal class PIDRContext
    {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private PIReaderConfig config;
		private string configFileFullPath;
		private Reader reader;
		private AbstractMQTTWriter mqttWriter;
		private string machineName = Environment.MachineName;
		private FileWriter fileWriter;
		private Dictionary<string, string> topicsMap;
		private Dictionary<string, List<ReadInterval>> nextReadIntervalsByEquipment;
		Dictionary<string, string> lastReadTimesByEquipment;
		private ReadIntervalsManager readIntervalsMgr = new ReadIntervalsManager();
		private Mailer mailer;
		private bool executeDummyReadFirst;
		private bool isWindowsService;
		private bool isMQTTEnabled = true;

		public PIDRContext(bool isWindowsService) {
			this.isWindowsService = isWindowsService;
		}

		public Dictionary<string, string> getTopicsMap() {
			return topicsMap;
		}

		public bool getIsWindowsService() {
			return isWindowsService;
		}

		public bool getIsReadExtentFrequency() {
			return ReadExtent.READ_EXTENT_FREQUENCY.Equals(config.read.readExtent.type.ToLower());
		}

		public bool isMqttEnabled() {
			return isMQTTEnabled;
		}

		public void setConfigfileFullPath(string configFileFullPath) {
			this.configFileFullPath = configFileFullPath;
		}

		public void setupReadIntervals() {
			readIntervalsMgr.setupReadIntervals(config);
		}

		public void setupReadIntervals(Dictionary<string, string> lastReadTimesByEquipment) {
			readIntervalsMgr.setupReadIntervals(config, lastReadTimesByEquipment);
		}

		public Dictionary<string, List<ReadInterval>> getNextReadIntervalsByEquipment() {
			if (null != readIntervalsMgr) {
				return readIntervalsMgr.getNextreadIntervalsByEquipment();
			}
			return null;
		}

		public FileWriter getFileWriter() {
			return fileWriter;
		}

		public AbstractMQTTWriter getMqttWriter() {
			return mqttWriter;
		}

		public Reader getReader() {
			return reader;
		}

		public PIReaderConfig getConfig() {
			return config;
		}

		public Mailer getMailer() {
			return mailer;
		}

		public bool executeDummyRead() {
			return executeDummyReadFirst;
		}

		public int init(bool dumpReadsToLocalFiles) {
			if (!createConfiguration()) {
				logger.Fatal("Cannot read configuration file or configuration file is not valid. Program will abort.");
				return ExitCodes.EXITCODE_INVALIDCONFIG;
			}
			
			int errCode = PIReaderConfig.checkValid(config);
			if (ConfigurationErrors.CFGERR_NONE != errCode) {
				ConfigurationErrors cfgErr = new ConfigurationErrors();
				logger.Fatal("Error validating configuration. Details: {0}", cfgErr[errCode]);
				logger.Fatal("Program will abort.");
				return ExitCodes.EXITCODE_INVALIDCONFIG;
			}

			logger.Info("Using valid configuration file at: {0}", configFileFullPath);

			int res = checkServiceCompatibility();
			if (ExitCodes.EXITCODE_SUCCESS != res) {
				return res;
			}
			
			if (config.read.readExtent.type.Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				executeDummyReadFirst = true;
			}

			Connection connection = config.getConnectionByName("pi");
			try {
				reader = new Reader(
					connection.getParameterValueByName(Parameter.PARAMNAME_PISERVERNAME),
					connection.getParameterValueByName(Parameter.PARAMNAME_PISDKTYPE),
					config.read.readBatches(),
					config.dateFormats.reference,
					config.dateFormats.pi,
					config.separators.timestampSeparator,
					config.separators.fieldSeparator,
					config.separators.valueSeparator);
				reader.init();
			} catch (Exception) {
				logger.Fatal("Unable to create reader. Program will abort.");
				return ExitCodes.EXITCODE_CANNOTCREATEREADER;
			}

			Connection mqttConnection = config.getConnectionByName("mqtt");
			isMQTTEnabled = mqttConnection.isEnabled();
			string clientName = "." + Utils.md5Calc(machineName + configFileFullPath);
			if (isMQTTEnabled) { 
				string brokerAddress = mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTBROKERADDRESS);
				string brokerPort = mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTBROKERPORT);
				ushort keepAliveSec = Connection.DEFAULT_MQTT_KEEPALIVE_SEC;
				try {
					keepAliveSec = ushort.Parse(mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTKEEPALIVESEC));
				} catch (Exception) {
					logger.Warn("Unable to parse valid keep alive value. Reverting to default value of {1}s.", mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTKEEPALIVESEC), keepAliveSec);
				}
				try {
					clientName = mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTCLIENTNAME) + clientName;
					if(null != mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTCLIENTTYPE) &&
						mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTCLIENTTYPE).Equals(Parameter.PARAM_VALUE_MQTTCLIENTTYPE_MQTTNET)) {
						mqttWriter = MQTTWriterFactory.createMQTTNet(brokerAddress, brokerPort, clientName, keepAliveSec);
					} else {
						mqttWriter = MQTTWriterFactory.createM2MQTT(brokerAddress, brokerPort, clientName, keepAliveSec);
					}
				} catch (Exception) {
					logger.Fatal("Unable to create MQTT writer. Program will abort.");
					return ExitCodes.EXITCODE_CANNOTCREATEWRITER;
				}
			}

			if (dumpReadsToLocalFiles) {
				string currentWorkingFolder = System.IO.Path.GetDirectoryName(configFileFullPath);
				fileWriter = new FileWriter(currentWorkingFolder, config.dateFormats.reference, config.dateFormats.hadoop, config.separators.timestampSeparator[0], config.separators.valueSeparator[0], config.separators.fieldSeparator[0]);
			}

			try {
				mailer = new Mailer(config.mailData.smtphost, config.mailData.from, config.mailData.to, config.mailData.subject, config.mailData.body);
				mailer.enabled = config.mailData.enabled;
				mailer.setMachineName(machineName);
				mailer.setMqttclientName(clientName);
			} catch (Exception) {
				logger.Fatal("Unable to mailer object. Program will abort.");
				return ExitCodes.EXITCODE_CANNOTCREATEMAILER;
			}

			try { 
				buildTopicsMap();
			} catch(Exception) {
				logger.Fatal("Cannot create collection that maps equipments to topics");
				return ExitCodes.EXITCODE_INVALIDCONFIG;
			}

			if (null == readIntervalsMgr) {
				logger.Fatal("Null reference to ReadIntervalsManager. Unable to setup read intervals. Program will abort.");
				return ExitCodes.EXITCODE_INVALIDREADINTERVALS;
			}

			try {
				readIntervalsMgr.setupReadIntervals(config, lastReadTimesByEquipment);
				nextReadIntervalsByEquipment = readIntervalsMgr.getNextreadIntervalsByEquipment();
			} catch (Exception ex) {
				logger.Fatal("Unable to setup read intervals. Program will abort.");
				logger.Fatal("Details: {0}", ex.ToString());
				return ExitCodes.EXITCODE_INVALIDREADINTERVALS;
			}
			return ExitCodes.EXITCODE_SUCCESS;
		}

		internal void getLastReadTimesByEquipmentFromLog() {
			lastReadTimesByEquipment = Utils.getLastReadTimesByEquipmentFromLog();
		}

		private int checkServiceCompatibility() {
			int res = ExitCodes.EXITCODE_SUCCESS;
			if (isWindowsService) {
				if (!config.read.readExtent.type.Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
					logger.Fatal("Service can run only if ReadExtent is set to \"frequency\"");
					res = ExitCodes.EXITCODE_INVALIDREADEXTENT;
				}
			} else {
				/*
				if (config.read.readExtent.type.Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
					logger.Fatal("PI Data Reader can run as an exe only if ReadExtent is set to \"fixed\" or \"relative\"");
					res = ExitCodes.EXITCODE_INVALIDREADEXTENT;
				}
				*/
			}
			return res;
		}

		private bool createConfiguration() {
			try {
				PIReaderConfig.xmlValidate(configFileFullPath);
				config = PIReaderConfig.parseFromFile(configFileFullPath);
			} catch (Exception ex) {
				logger.Fatal("Unable to parse or validate configuration file from {0}. Please check that:\n1. the file exists and is well formed;\n2. xmlns=\"http://www.lilly.com/PIDR\" is specified in the root element;\n3. XML schema file is referenced correctly.\n", configFileFullPath);
				logger.Fatal("Details: {0}", ex.Message);
				return false;
			}
			return true;
		}

		private void buildTopicsMap() {
			topicsMap = new Dictionary<string, string>();
			if (config.read.readMode.Equals(Read.READMODE_BATCH)) {
				foreach (BatchCfg bCfg in config.read.batches) {
					topicsMap.Add(bCfg.moduleName, bCfg.mqttTopic);
				}
			} else {
				foreach (EquipmentCfg eqCfg in config.read.equipments) {
					topicsMap.Add(eqCfg.name, eqCfg.mqttTopic);
				}
			}
		}

		
	}

	
}
