using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	public sealed class ExitCodes : Dictionary<int, string> {
		private static volatile ExitCodes instance;
		private static object syncRoot = new Object();
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

		private ExitCodes() {
			this.Add(EXITCODE_SUCCESS, "Success");
			this.Add(EXITCODE_INVALIDCOMMANDLINE, "Invalid command line");
			this.Add(EXITCODE_INVALIDCONFIG, "Invalid configuration file");
			this.Add(EXITCODE_INVALIDREADINTERVALS, "Invalid read intervals");
			this.Add(EXITCODE_CANNOTCREATEREADER, "Unable to create PI data reader");
			this.Add(EXITCODE_CANNOTCREATEWRITER, "Unable to create MQTT writer");
			this.Add(EXITCODE_CANNOTCREATEMAILER, "Unable to create mailer");
			this.Add(EXITCODE_VERSION, "Exe launched with version option");
			this.Add(EXITCODE_INVALIDREADEXTENT, "Invalid read extent");
			this.Add(EXITCODE_INVALIDXMLINCONFIG, "Invalid XML in config file");
		}

		public static ExitCodes Instance {
			get {
				if (instance == null) {
					lock (syncRoot) {
						if (instance == null)
							instance = new ExitCodes();
					}
				}
				return instance;
			}
		}
	}
}
