using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	public class ReadIntervalsManager {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private Dictionary<string, ReadInterval> nextReadIntervalsByEquipment;

		public Dictionary<string, ReadInterval> getNextreadIntervalsByEquipment() {
			return nextReadIntervalsByEquipment;
		}

		/*
		 * Initial setup of read intervals
		 * */
		public void setupReadIntervals(PIReaderConfig config, Dictionary<string, string> lastReadTimesByEquipment) {
			DateTime nowDT = DateTime.Now;
			DateTime startTimeFromConfig = setupStartTime(config.read.readExtent, config.dateFormats.reference, nowDT);
			DateTime endTimeFromConfig = setupEndTime(config.read.readExtent, config.dateFormats.reference, nowDT);

			logger.Info("Read interval (from configuration file parameters) is: [{0}, {1}]", startTimeFromConfig.ToString(config.dateFormats.reference), endTimeFromConfig.ToString(config.dateFormats.reference));

			nextReadIntervalsByEquipment = new Dictionary<string, ReadInterval>();

			if (config.read.readBatches()) {
				foreach (BatchCfg batchCfg in config.read.batches) {
					nextReadIntervalsByEquipment.Add(batchCfg.moduleName, new ReadInterval(startTimeFromConfig, endTimeFromConfig));
				}
			} else {
				foreach (EquipmentCfg eq in config.read.equipments) {
					nextReadIntervalsByEquipment.Add(eq.name, new ReadInterval(startTimeFromConfig, endTimeFromConfig));
				}
			}
			//when a reader is scheduled, if read times read from log are "before" 
			//those specified in the config, use read times from log
			if (config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				foreach (string eqmName in nextReadIntervalsByEquipment.Keys) {
					if (lastReadTimesByEquipment.ContainsKey(eqmName)) {
						string dtString = lastReadTimesByEquipment[eqmName];
						DateTime dt = DateTime.ParseExact(dtString, config.dateFormats.reference, CultureInfo.InvariantCulture);

						//check if date selected from log implies having a read interval which is too long
						DateTime nextIntervalEnd = nextReadIntervalsByEquipment[eqmName].end;
						long readBackLimitSec = config.read.readExtent.readExtentFrequency.getReadbackLimitSeconds();
						DateTime limitStart = nextIntervalEnd.AddSeconds(-readBackLimitSec);

						if (readBackLimitSec != 0 && dt < limitStart) {
							dt = limitStart;
							logger.Info("Cannot use last read date contained in log file: this would result in a read interval which exceeds {0} seconds.", readBackLimitSec);
						}

						if (dt < nextReadIntervalsByEquipment[eqmName].start) {
							nextReadIntervalsByEquipment[eqmName].start = dt;
							logger.Info("Overridden read start date for equipment {0} to {1}", eqmName, dt.ToString(config.dateFormats.reference));
						}
					}
				}
			}
		}

		/*
		 * Subsequent setup of read intervals (when schedule is based on frequency)
		 * */
		public void setupReadIntervals(PIReaderConfig config) {
			DateTime nowDT = DateTime.Now;
			DateTime startTimeFromConfig = setupStartTime(config.read.readExtent, config.dateFormats.reference, nowDT);
			DateTime endTimeFromConfig = setupEndTime(config.read.readExtent, config.dateFormats.reference, nowDT);

			logger.Trace("Read interval (from configuration file parameters) is: [{0}, {1}]", startTimeFromConfig.ToString(config.dateFormats.reference), endTimeFromConfig.ToString(config.dateFormats.reference));

			foreach (string eqmName in nextReadIntervalsByEquipment.Keys) {
				ReadInterval ri = nextReadIntervalsByEquipment[eqmName];
				if (ri.end < startTimeFromConfig) {
					ri.start = ri.end;
					logger.Trace("End time of previous interval is before start of next interval. Risk of losing data. Adjusted next read intervals.");
				}
				ri.end = endTimeFromConfig;
			}
		}

		private DateTime setupStartTime(ReadExtent readExtent, string dateFormat, DateTime nowDT) {
			logger.Info("Read extent set to {0}", readExtent.type);
			if (readExtent.type.Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				double readBackSec = readExtent.readExtentFrequency.getReadBackSecondsAsDouble();
				return nowDT.AddSeconds(-(readBackSec));
			}
			if (readExtent.type.Equals(ReadExtent.READ_EXTENT_FIXED)) {
				return DateTime.ParseExact(readExtent.readExtentFixed.startdate, dateFormat, CultureInfo.InvariantCulture);
			}
			if (readExtent.type.Equals(ReadExtent.READ_EXTENT_RELATIVE)) {
				double readBackSec = readExtent.readExtentRelative.getReadBackSecondsAsDouble();
				return nowDT.AddSeconds(-(readBackSec));
			}
			throw new Exception("Invalid ReadExtent. Please check your xml configuration file.");
		}

		private DateTime setupEndTime(ReadExtent readExtent, string dateFormat, DateTime nowDT) {
			if (readExtent.type.Equals(ReadExtent.READ_EXTENT_FIXED)) {
				if (null == readExtent.readExtentFixed) {
					throw new Exception("Null ReadExtentFixed reference. Cannot setup end time.");
				}
				return DateTime.ParseExact(readExtent.readExtentFixed.enddate, dateFormat, CultureInfo.InvariantCulture);
			}
			return nowDT;
		}
	}
}
