using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	public class Options {
		[Option('c', "config", Required = true, HelpText = "Full path to configuration file")]
		public string ConfigFileFullPath { get; set; }

		[Option('f', "file", Required = true, HelpText = "Dump reads to local file (create/append/none)")]
		public string DumpReadsToLocalFile { get; set; }

		[Option('v', "version", Required = false, HelpText = "Displays application version")]
		public bool Version { get; set; }

		[ParserState]
		public IParserState LastParserState { get; set; }

		[HelpOption]
		public string GetUsage() {
			return HelpText.AutoBuild(this,
			  (HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}
}
