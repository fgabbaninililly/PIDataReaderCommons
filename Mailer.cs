using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PIDataReaderCommons {
	public class Mailer {
		private static Logger logger = LogManager.GetCurrentClassLogger();

		private string smtpHost;
		private MailMessage mailMessage;

		public bool enabled { get; set; }
		public string clientName { get; set; }
		public string machineName { get; set; }

		public Mailer(string smtpHost, string from, string to, string subject, string body) {
			this.smtpHost = smtpHost;
			//this.from = from;
			//this.to = to;
			//this.subject = subject;
			//this.body = body;
			//this.body.Replace(@"\n", Environment.NewLine);
			
			mailMessage = new MailMessage();
			mailMessage.From = new MailAddress(from);

			string[] recipientList = to.Split(';', ',');
			foreach(string recipient in recipientList) {
				mailMessage.To.Add(new MailAddress(recipient));
			}

			mailMessage.Subject = subject;
			mailMessage.Body = body;
		}
				
		public void send() {
			try { 
				SmtpClient smtp = new SmtpClient(smtpHost);
				//smtp.EnableSsl = true;
				//smtp.Port = 587;
				//smtp.Credentials = new NetworkCredential("email@gmail.com", "appspecificpassword");
				//smtp.Send(from, to, subject, body);
				smtp.Send(mailMessage);
				logger.Info("Sent email message. Smtp: {0}, from: {1}, to: {2}, subject: {3}, body: {4}", smtpHost, mailMessage.From.Address, mailMessage.To.ToString(), mailMessage.Subject, mailMessage.Body);
			} catch (SmtpException smtpExc) {
				logger.Error("Error sending email through the smtp client. Details: {0}", smtpExc.Message);
			} catch (Exception e) {
				logger.Error("Error sending email. Details: {0}", e.Message);
			}
		}

		internal void setTime(DateTime dt) {
			try {
				string dateString = dt.ToString("yyyy-MM-ddTHH-mm-ss");
				mailMessage.Body = mailMessage.Body.Replace("[[time]]", dateString);
			} catch(Exception e) {
				logger.Error("Error formatting time. Details: {0}", e.Message);
			}
		}

		internal void setMachineName(string machineName) {
			this.machineName = machineName;
			mailMessage.Body = mailMessage.Body.Replace("[[machinename]]", machineName);
		}

		internal void setMqttclientName(string clientName) {
			mailMessage.Body = mailMessage.Body.Replace("[[mqttclientname]]", clientName);
		}
	}
}
