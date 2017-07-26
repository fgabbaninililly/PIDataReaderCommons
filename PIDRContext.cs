using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons
{
	public class ExitCodes {
		public static readonly int EXITCODE_SUCCESS = 0;
		public static readonly int EXITCODE_INVALIDCOMMANDLINE = -1;
		public static readonly int EXITCODE_INVALIDCONFIG = -2;
		public static readonly int EXITCODE_INVALIDREADINTERVALS = -3;
		public static readonly int EXITCODE_CANNOTCREATEREADER = -4;
		public static readonly int EXITCODE_CANNOTCREATEWRITER = -5;
		public static readonly int EXITCODE_CANNOTCREATEMAILER = -6;
		public static readonly int EXITCODE_VERSION = -7;
		public static readonly int EXITCODE_INVALIDREADEXTENT = -8;
		public static readonly int EXITCODE_INVALIDXMLINCONFIG = -9;
	}

    public class PIDRContext
    {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private PIReaderConfig config;
		private string configFileFullPath;
		private Reader reader;
		private MQTTWriter mqttWriter;
		private string machineName = Environment.MachineName;
		private FileWriter fileWriter;
		private Dictionary<string, string> topicsMap;
		private Dictionary<string, ReadInterval> nextReadIntervalsByEquipment;
		Dictionary<string, string> lastReadTimesByEquipment;
		private ReadIntervalsManager readIntervalsMgr = new ReadIntervalsManager();
		private Mailer mailer;
		private bool executeDummyReadFirst;
		private bool isWindowsService;

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

		public void setConfigfileFullPath(string configFileFullPath) {
			this.configFileFullPath = configFileFullPath;
		}

		public void setupReadIntervals() {
			readIntervalsMgr.setupReadIntervals(config);
		}

		public void setupReadIntervals(Dictionary<string, string> lastReadTimesByEquipment) {
			readIntervalsMgr.setupReadIntervals(config, lastReadTimesByEquipment);
		}

		public Dictionary<string, ReadInterval> getNextreadIntervalsByEquipment() {
			if (null != readIntervalsMgr) {
				return readIntervalsMgr.getNextreadIntervalsByEquipment();
			}
			return null;
		}

		public FileWriter getFileWriter() {
			return fileWriter;
		}

		public MQTTWriter getMqttWriter() {
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
					config.dateFormats.pi);
				reader.init();
			} catch (Exception) {
				logger.Fatal("Unable to create reader. Program will abort.");
				return ExitCodes.EXITCODE_CANNOTCREATEREADER;
			}

			Connection mqttConnection = config.getConnectionByName("mqtt");
			string clientName = Utils.md5Calc(machineName + configFileFullPath);
			try {
				clientName = mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTCLIENTNAME) + clientName;
				mqttWriter = new MQTTWriter(
						mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTBROKERADDRESS),
						mqttConnection.getParameterValueByName(Parameter.PARAMNAME_MQTTBROKERPORT),
						clientName
						);
				
			} catch (Exception) {
				logger.Fatal("Unable to create MQTT writer. Program will abort.");
				return ExitCodes.EXITCODE_CANNOTCREATEWRITER;
			}

			if (dumpReadsToLocalFiles) {
				string currentWorkingFolder = System.IO.Path.GetDirectoryName(configFileFullPath);
				fileWriter = new FileWriter(currentWorkingFolder, config.dateFormats.reference, config.dateFormats.hadoop);
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

			buildTopicsMap();

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
				config = PIReaderConfig.parseFromFile(configFileFullPath);
			} catch (Exception ex) {
				logger.Fatal("Unable to parse configuration file from {0}. Please check that the file exists and is well formed.", configFileFullPath);
				logger.Fatal("Details: {0}", ex.ToString());
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
