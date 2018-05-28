using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PIDataReaderCommons {
	public class PIDRController {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private static string currentWorkingFolder;

		private bool dumpReadsToLocalFiles;
		private bool appendToLocalFiles;
				
		private string mainAssemblyVersionInfo;
		private string piDataReaderLibVersionInfo;

		private PIDRContext pidrContext;
		private Reader reader;
		private AbstractMQTTWriter mqttWriter;
		private FileWriter fileWriter;
		private Options options;

		private Timer timer;
		private UInt32 timerPeriod;

		private List<MQTTPublishTerminatedEventArgs> publishTerminatedEAList;
		private List<PIReadTerminatedEventArgs> readTerminatedEAList;

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
				logger.Info("{0}", Version.getVersion());
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
				logger.Fatal("Cannot find valid configuration file at {0}", options.ConfigFileFullPath);
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
			logger.Info("{0}", Version.getVersion());
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

		public bool isScheduled() {
			return pidrContext.getIsReadExtentFrequency();
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

			if (!mqttWriter.isConnected()) {
				return ExitCodes.EXITCODE_CANNOTCONNECTTOBROKER;
			}

			mqttWriter.MQTTWriter_PublishCompleted += MQTTWriter_PublishCompleted;
			mqttWriter.MQTTWriter_ClientClosed += MqttWriter_MQTTWriter_ClientClosed;

			logger.Info("MQTT data writer was successfully created");
			logger.Info("MQTT client ID is {0}", mqttWriter.getClientName());

			fileWriter = pidrContext.getFileWriter();

			if (pidrContext.getIsReadExtentFrequency()) {
				startTimer();
			} else {
				startOneShot();
			}

			return ExitCodes.EXITCODE_SUCCESS;
		}

		public int stop() {
			timer.Change(Timeout.Infinite, Timeout.Infinite);
			timer.Dispose();
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

			readAndWrite();

			timerPeriod = (UInt32)(re.readExtentFrequency.getReadBackSecondsAsDouble() * 1000);
			timer = new Timer(Timer_Elapsed, 0, timerPeriod, timerPeriod);
		}

		private void startOneShot() {
			logger.Info("Start single run");
			readAndWrite();
			if (null != mqttWriter) {
				mqttWriter.close();
			}
		}

		private void readAndWrite() {
			Dictionary<string, string> topicsMap = pidrContext.getTopicsMap();
			PIReaderConfig config = pidrContext.getConfig();
			publishTerminatedEAList = new List<MQTTPublishTerminatedEventArgs>();
			readTerminatedEAList = new List<PIReadTerminatedEventArgs>();
			try {
				if (config.read.readMode.Equals(Read.READMODE_BATCH)) {
					logger.Info("====Start reading/writing batch info");
					//for each module and for each interval
					foreach (BatchCfg batchCfg in config.read.batches) {
						logger.Info("====Module '{0}'", batchCfg.moduleName);
						List<ReadInterval> readIntervals = pidrContext.getNextReadIntervalsByEquipment()[batchCfg.moduleName];
						foreach (ReadInterval readInterval in readIntervals) {
							logger.Info("======Time interval: [{0}, {1}]", readInterval.start.ToString(config.dateFormats.reference), readInterval.end.ToString(config.dateFormats.reference));
							PIData piData = reader.readBatches(batchCfg, readInterval);
							writeReadFinishedToLog(piData, batchCfg.moduleName);
							mqttWriter.write(piData, batchCfg.moduleName, topicsMap);
							if (dumpReadsToLocalFiles) {
								fileWriter.writeBatches(piData, batchCfg.moduleName, appendToLocalFiles);
							}
						}
					}
				} else {
					logger.Info("====Start reading/writing tag info");
					//for each equipment and for each interval
					foreach (EquipmentCfg equipmentCfg in config.read.equipments) {
						logger.Info("====Equipment '{0}'", equipmentCfg.name);
						List<ReadInterval> readIntervals = pidrContext.getNextReadIntervalsByEquipment()[equipmentCfg.name];
						foreach (ReadInterval readInterval in readIntervals) {
							logger.Info("======Time interval: [{0}, {1}]", readInterval.start.ToString(config.dateFormats.reference), readInterval.end.ToString(config.dateFormats.reference));
							PIData piData = reader.readTags(equipmentCfg, readInterval);
							if (null != piData) {
								writeReadFinishedToLog(piData, equipmentCfg.name);
								mqttWriter.write(piData, equipmentCfg.name, topicsMap);
								if (dumpReadsToLocalFiles) {
									fileWriter.writeTags(piData, equipmentCfg.name, appendToLocalFiles);
								}
							}
						}
					}
				}
				readAndWriteCompleted();
			} catch (Exception ex) {
				logger.Fatal("Exception encountered while reading data or publishing data or writing data to file. Details {0}.", ex.ToString());
			} finally { }
		}

		private void readAndWriteCompleted() {
			ulong totalRecordCount = 0, totalMessageCount = 0, totalByteCount = 0;
			double totalReadTime = 0, totalPublishTime = 0;

			foreach(PIReadTerminatedEventArgs ea in readTerminatedEAList) {
				totalRecordCount += ea.recordCount;
				totalReadTime += ea.elapsedTime;
			}
			foreach(MQTTPublishTerminatedEventArgs ea in publishTerminatedEAList) {
				totalMessageCount += ea.messageCount;
				totalByteCount += ea.byteCount;
				totalPublishTime += ea.elapsedTime;
			}

			logger.Info("====End reading/writing. Summary of performances");
			logger.Info("====Read from PI. Total records: {0}. Total time: {1}", totalRecordCount, totalReadTime);
			double throughput = 0;
			if (0 != totalPublishTime) {
				throughput = totalByteCount / totalPublishTime;
			}
			logger.Info("====MQTT Publish. Total messages: {0}. Total bytes: {1}. Total time: {2}. Avg. thrput: {3} bytes/s", totalMessageCount, totalByteCount, totalPublishTime, throughput.ToString("F2"));
			logger.Info("====Total time (read + publish): {0}", totalReadTime + totalPublishTime);

			PIReaderConfig config = pidrContext.getConfig();
			if (config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				double scheduleAsSec = config.read.readExtent.readExtentFrequency.getFrequencySecondsAsDouble();
				if ((totalPublishTime + totalReadTime) > scheduleAsSec) {
					logger.Warn("TIME REQUIRED FOR READING AND POSTING EXCEEDS SCHEDULE PERIOD: CONSIDER SCHEDULING AT A LOWER FREQUENCY!");
				}
			}
		}

		private void writeReadFinishedToLog(PIData piData, string equipmentName) {
			try {
				logger.Info("{0}{1}{2},{3}", Utils.READEND_MARKER, Utils.READEND_SEPARATOR, equipmentName, piData.readFinished);
			} catch (Exception e) {
				logger.Error(e.ToString());
			}
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
		private void Timer_Elapsed(Object state) {
			logger.Trace("Timer elapsed");
			timer.Change(Timeout.Infinite, Timeout.Infinite);

			Stopwatch sw = new Stopwatch();
			sw.Start();

			pidrContext.setupReadIntervals();
			readAndWrite();

			sw.Stop();
			long elapsedMS = sw.ElapsedMilliseconds;
			long nextTimerStart = Math.Max(0, timerPeriod - elapsedMS);

			//Trigger next timer:
			//1. immediately (nextTimerStart = 0), if the previous execution took more than timerPeriod
			//2. after timerPeriod - elapsedMS, if the previous run took less than timerPeriod
			//We are trying to run the timer following its originally scheduled period, but we want the next execution to be 
			//started only when the previous one is completed.

			timer.Change(nextTimerStart, timerPeriod);
		}

		private void Timer_Disposed(object sender, EventArgs e) {
			logger.Info("Timer disposed");
			if (null != mqttWriter) {
				mqttWriter.close();
			}
		}

		private void Reader_PIReadTerminated(PIReadTerminatedEventArgs e) {
			readTerminatedEAList.Add(e);
			double throughput = 0;
			if (0 != e.elapsedTime) {
				throughput = e.recordCount / e.elapsedTime;
			}
			logger.Info("Read complete. Time {0}s. Records: {1}. Thrput: {2} records/s. ", e.elapsedTime, e.recordCount, throughput.ToString("F2"));
		}

		private void MQTTWriter_PublishCompleted(MQTTPublishTerminatedEventArgs e) {
			publishTerminatedEAList.Add(e);
			double throughput = 0;
			if (0 != e.elapsedTime) {
				throughput = e.byteCount / e.elapsedTime;
			}
			logger.Info("Publish complete. Time {0}s. Bytes: {1}. Thrput: {2} bytes/s. ", e.elapsedTime, e.byteCount, throughput.ToString("F2"));
		}

		private void MqttWriter_MQTTWriter_ClientClosed(MQTTClientClosedEventArgs e) {
		}
		
		#endregion
	}
}
