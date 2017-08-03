using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;

namespace PIDataReaderCommons {
	public class PIDRController {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private static string currentWorkingFolder;

		private bool dumpReadsToLocalFiles;
		private bool appendToLocalFiles;
		private double overallReadTime;
		
		private string mainAssemblyVersionInfo;
		private string piDataReaderLibVersionInfo;

		private PIDRContext pidrContext;
		private Reader reader;
		private MQTTWriter mqttWriter;
		private FileWriter fileWriter;
		private Options options;

		private System.Timers.Timer timer;
		
		public PIDRController(string mainAssemblyVersionInfo, bool isWindowsService) {
			this.mainAssemblyVersionInfo = mainAssemblyVersionInfo;
			this.piDataReaderLibVersionInfo = PIDataReaderLib.Version.getVersion();
			pidrContext = new PIDRContext(isWindowsService);
		}

		private int parseCommandLine(string[] args) {
			options = new Options();
			bool cmdLineParseOk = CommandLine.Parser.Default.ParseArguments(args, options);
			if (options.Version) {
				logger.Info("{0}", mainAssemblyVersionInfo);
				logger.Info("{0}", piDataReaderLibVersionInfo);
				return ExitCodes.EXITCODE_VERSION;
			}

			if (!cmdLineParseOk) {
				logger.Fatal("Error reading command line options");
				return ExitCodes.EXITCODE_INVALIDCOMMANDLINE;
			}
			return ExitCodes.EXITCODE_SUCCESS;
		}

		private int setupOptions() {
			if (!File.Exists(options.ConfigFileFullPath)) {
				return ExitCodes.EXITCODE_INVALIDCONFIG;
			}
			
			pidrContext.setConfigfileFullPath(options.ConfigFileFullPath);
			
			if ("append".Equals(options.DumpReadsToLocalFile.ToLower())) {
				dumpReadsToLocalFiles = true;
				appendToLocalFiles = true;
			}
			if ("create".Equals(options.DumpReadsToLocalFile.ToLower())) {
				dumpReadsToLocalFiles = true;
				appendToLocalFiles = false;
			}

			currentWorkingFolder = System.IO.Path.GetDirectoryName(options.ConfigFileFullPath);
			Utils.redirectLogFile(currentWorkingFolder + @"\_logs\");

			//read last read times from log file before any other component accesses it
			pidrContext.getLastReadTimesByEquipmentFromLog();

			logger.Info(">>>Starting reader<<<");
			logger.Info("{0}", mainAssemblyVersionInfo);
			logger.Info("{0}", piDataReaderLibVersionInfo);
			logger.Info("Log file redirected to {0}", currentWorkingFolder + @"\_logs\");
			
			return ExitCodes.EXITCODE_SUCCESS;
		}

		private int initializeContext() {
			int res = pidrContext.init(dumpReadsToLocalFiles);
			if (ExitCodes.EXITCODE_SUCCESS != res) {
				return res;
			}
			return res;
		}
		
		public int start(string[] args) {
			
			int res = parseCommandLine(args);
			if (ExitCodes.EXITCODE_SUCCESS != res) {
				return res;
			}

			res = setupOptions();
			if (ExitCodes.EXITCODE_SUCCESS != res) {
				return res;
			}
			logger.Info("Configuration file and logger initialized correctly");

			res = initializeContext();
			if (ExitCodes.EXITCODE_SUCCESS != res) {
				logger.Fatal("Cannot initialize context. Reader and Writer are not available. Error code {0}.", res);
				return res;
			}
			logger.Info("Context was initialized correctly. Reader and Writer initialized correctly.");

			reader = pidrContext.getReader();
			reader.Reader_PIReadTerminated += Reader_PIReadTerminated;
			if (pidrContext.executeDummyRead()) { 
				reader.dummyRead();
			}

			mqttWriter = pidrContext.getMqttWriter();
			mqttWriter.initAndConnect();
			mqttWriter.MQTTWriter_PublishCompleted += MQTTWriter_PublishCompleted;
			mqttWriter.MQTTWriter_ClientClosed += MqttWriter_MQTTWriter_ClientClosed;
			logger.Info("MQTT data writer was successfully created");
			logger.Info("MQTT client name is {0}", mqttWriter.getClientName());

			fileWriter = pidrContext.getFileWriter();

			if (pidrContext.getIsReadExtentFrequency()) {
				startTimer();
			} else {
				startOneShot();
			}


			return ExitCodes.EXITCODE_SUCCESS;
		}

		public int stop() {
			timer.Stop();
			logger.Info("Timer was stopped");
			return ExitCodes.EXITCODE_SUCCESS;
		}

		public void sendMail() {
			Mailer mailer = pidrContext.getMailer();
			if (null == mailer) {
				logger.Error("Null mailer!! This should not really happen...");
				return;
			}

			try {
				if (mailer.enabled) {
					mailer.setTime(DateTime.Now);
					mailer.send();
					logger.Info("Mail successfully sent.");
				} else {
					logger.Info("Mailer not enabled. No mail sent.");
				}
			} catch (Exception ex) {
				logger.Error("Error sending email. Details: {0}", ex.Message);
			}
		}

		private void startTimer() {
			logger.Info("Start running using scheduled timer");
			ReadExtent re = pidrContext.getConfig().read.readExtent;
			timer = new System.Timers.Timer();
			timer.Interval = re.readExtentFrequency.getReadBackSecondsAsDouble() * 1000;
			timer.Elapsed += Timer_Elapsed;
			timer.Disposed += Timer_Disposed;
			timer.Start();
			readAndWrite();
		}

		private void startOneShot() {
			logger.Info("Start single run");
			readAndWrite();
			if (null != mqttWriter) {
				mqttWriter.close();
			}
		}

		private void readAndWrite() {
			Dictionary<string, PIData> piDataMap = null;
			Dictionary<string, string> topicsMap = pidrContext.getTopicsMap();
			PIReaderConfig config = pidrContext.getConfig();
			try {
				if (config.read.readMode.Equals(Read.READMODE_BATCH)) {
					logger.Info("Reading batch information");
					piDataMap = reader.readBatches(config, pidrContext.getNextreadIntervalsByEquipment());
					writeReadFinishedToLog(piDataMap);
					mqttWriter.write(piDataMap, topicsMap);
					if (dumpReadsToLocalFiles) {
						fileWriter.writeBatches(piDataMap, appendToLocalFiles);
					}
				} else {
					logger.Info("Reading tag information");
					piDataMap = reader.readTags(config, pidrContext.getNextreadIntervalsByEquipment());
					if (piDataMap.Count > 0) {
						writeReadFinishedToLog(piDataMap);
						mqttWriter.write(piDataMap, topicsMap);
						if (dumpReadsToLocalFiles) {
							fileWriter.writeTags(piDataMap, appendToLocalFiles);
						}
					}
				}
			} catch (Exception ex) {
				logger.Fatal("Exception encountered while reading data or publishing data or writing data to file. Details {0}.", ex.ToString());
			} finally { }
		}

		private void writeReadFinishedToLog(Dictionary<string, PIData> piDataMap) {
			foreach (string equipmentName in piDataMap.Keys) {
				try {
					PIData piData = piDataMap[equipmentName];
					logger.Info("{0}{1}{2},{3}", Utils.READEND_MARKER, Utils.READEND_SEPARATOR, equipmentName, piData.readFinished);
				} catch (Exception e) {
					logger.Error(e.ToString());
				}
			}
		}

		#region event handlers
		private void Timer_Elapsed(object sender, ElapsedEventArgs e) {
			logger.Trace("Timer elapsed");
			pidrContext.setupReadIntervals();
			readAndWrite();
		}

		private void Timer_Disposed(object sender, EventArgs e) {
			logger.Trace("Timer disposed");
			if (null != mqttWriter) {
				mqttWriter.close();
			}
		}

		private void Reader_PIReadTerminated(PIReadTerminatedEventArgs e) {
			overallReadTime = 0;
			foreach (string eqmName in e.readTimesByEquipment.Keys) {
				logger.Info("Time required to read tags/batches for equipment/module {0}: {1}", eqmName, e.readTimesByEquipment[eqmName]);
				overallReadTime += e.readTimesByEquipment[eqmName];
			}
			logger.Info("Total time required for reading {0} equipments/modules: {1}", e.readTimesByEquipment.Count, overallReadTime);
		}

		private void MQTTWriter_PublishCompleted(MQTTPublishTerminatedEventArgs e) {
			double totalReadAndPublishTimeSec = e.elapsedTimeSec + overallReadTime;

			logger.Info("Total time required for publish: {0}s", e.elapsedTimeSec.ToString());
			logger.Info("Total time required for reading and publishing: {0}s", totalReadAndPublishTimeSec);
			logger.Info("Throughput in this scheduled run was: {0} bytes/s", e.throughput.ToString("F2"));
			PIReaderConfig config = pidrContext.getConfig();
			if (config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				double scheduleAsSec = config.read.readExtent.readExtentFrequency.getFrequencySecondsAsDouble();
				if (totalReadAndPublishTimeSec > scheduleAsSec) {
					logger.Warn("TIME REQUIRED FOR READING AND POSTING EXCEEDS SCHEDULE PERIOD: RISK OF LOSING DATA AND/OR OVERLOADING MQTT BROKER!");
				}
			}
		}

		private void MqttWriter_MQTTWriter_ClientClosed(MQTTClientClosedEventArgs e) {
		}
		
		#endregion
	}
}
