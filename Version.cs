using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	public class Version {
		public static readonly string version = "1.3.0";

		public static string getVersion() {
			return string.Format("PIDataReader Common Classes v{0}", version);
		}
	}
}
