using NLog;
using PIDataReaderLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	internal class ReadIntervalsManager {
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private Dictionary<string, List<ReadInterval>> nextReadIntervalsByEquipment;

		public Dictionary<string, List<ReadInterval>> getNextreadIntervalsByEquipment() {
			return nextReadIntervalsByEquipment;
		}

		private List<ReadInterval> cutIntervalIntoSlices(UInt64 sliceDurationMilliSec, DateTime startTime, DateTime endTime, string dateFormat) {
			List<ReadInterval> slicedReadIntervals = new List<ReadInterval>();
			DateTime currentStartTime = startTime;
			logger.Info("Cutting time interval [{0}, {1}] into slices of {2} milliseconds", startTime.ToString(dateFormat), endTime.ToString(dateFormat), sliceDurationMilliSec);
			while (currentStartTime < endTime) {
				DateTime currentEndTime = currentStartTime.AddMilliseconds(sliceDurationMilliSec - 1);
				if (currentEndTime > endTime) {
					slicedReadIntervals.Add(new ReadInterval(currentStartTime, endTime));
					logger.Info("Added slice: [{0}, {1}]", currentStartTime.ToString(dateFormat), endTime.ToString(dateFormat));
				} else {
					slicedReadIntervals.Add(new ReadInterval(currentStartTime, currentEndTime));
					logger.Info("Added slice: [{0}, {1}]", currentStartTime.ToString(dateFormat), currentEndTime.ToString(dateFormat));
				}
				currentStartTime = currentEndTime.AddMilliseconds(1);
			}
			return slicedReadIntervals;
		}

		/*
		 * Initial setup of read intervals
		 * */
		public void setupReadIntervals(PIReaderConfig config, Dictionary<string, string> lastReadTimesByEquipment) {
			DateTime nowDT = DateTime.Now;
			DateTime startTimeFromConfig = setupStartTime(config.read.readExtent, config.dateFormats.reference, nowDT);
			DateTime endTimeFromConfig = setupEndTime(config.read.readExtent, config.dateFormats.reference, nowDT);

			logger.Info("Read interval (from configuration file parameters) is: [{0}, {1}]", startTimeFromConfig.ToString(config.dateFormats.reference), endTimeFromConfig.ToString(config.dateFormats.reference));
			nextReadIntervalsByEquipment = new Dictionary<string, List<ReadInterval>>();

			//when a reader is run with a fixed/relative time interval, slice the time interval into 
			//pieces to avoid large reads on PI
			List<ReadInterval> readIntervals = new List<ReadInterval>();
			readIntervals.Add(new ReadInterval(startTimeFromConfig, endTimeFromConfig));
			if (config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_FIXED) ||
				config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_RELATIVE)) {
				if (config.read.readExtent.isSliced()) {
					readIntervals = cutIntervalIntoSlices(config.read.readExtent.getSliceDurationMillisecSec(), startTimeFromConfig, endTimeFromConfig, config.dateFormats.reference);
				}
			}

			if (config.read.readBatches()) {
				foreach (BatchCfg batchCfg in config.read.batches) {
					nextReadIntervalsByEquipment.Add(batchCfg.moduleName, readIntervals);
				}
			} else {
				foreach (EquipmentCfg eq in config.read.equipments) {
					nextReadIntervalsByEquipment.Add(eq.name, readIntervals);
				}
			}

			//when a reader is scheduled, if read times read from log are "before" 
			//those specified in the config, use read times from log
			if (config.read.readExtent.type.ToLower().Equals(ReadExtent.READ_EXTENT_FREQUENCY)) {
				foreach (string eqmName in nextReadIntervalsByEquipment.Keys) {
					ReadInterval readInterval = (nextReadIntervalsByEquipment[eqmName])[0];
					if (lastReadTimesByEquipment.ContainsKey(eqmName)) {
						string dtString = lastReadTimesByEquipment[eqmName];
						DateTime dt = DateTime.ParseExact(dtString, config.dateFormats.reference, CultureInfo.InvariantCulture);

						//check if date selected from log implies having a read interval which is too long
						DateTime nextIntervalEnd = readInterval.end;
						long readBackLimitSec = config.read.readExtent.readExtentFrequency.getReadbackLimitSeconds();
						DateTime limitStart = nextIntervalEnd.AddSeconds(-readBackLimitSec);

						if (readBackLimitSec != 0 && dt < limitStart) {
							dt = limitStart;
							logger.Info("Cannot use last read date contained in log file: this would result in a read interval which exceeds {0} seconds.", readBackLimitSec);
						}

						if (dt < readInterval.start) {
							readInterval.start = dt;
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

			logger.Info("Read interval (from configuration file parameters) is: [{0}, {1}]", startTimeFromConfig.ToString(config.dateFormats.reference), endTimeFromConfig.ToString(config.dateFormats.reference));

			foreach (string eqmName in nextReadIntervalsByEquipment.Keys) {
				//schedule based on frequency: no slicing means that we can have only one read interval per equipment
				ReadInterval ri = nextReadIntervalsByEquipment[eqmName][0];
				
				if (ri.end < startTimeFromConfig) {
					logger.Info("End time of previous interval is before start of next interval. Risk of losing data. Adjusted next read interval.");
				} else {
					logger.Trace("Start time of next interval is before end of previous interval. Adjusted next read interval.");
				}
				ri.start = ri.end;
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
