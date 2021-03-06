﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace S22.Imap {
	/// <summary>
	/// A helper class for reading mail message data and building a MailMessage
	/// instance out of it.
	/// </summary>
	internal static class MessageBuilder {
		/// <summary>
		/// Creates a new empty instance of the MailMessage class from a string
		/// containing a raw mail message header.
		/// </summary>
		/// <param name="text">A string containing the mail header to create
		/// the MailMessage instance from.</param>
		/// <returns>A MailMessage instance with initialized Header fields but
		/// no content</returns>
		internal static MailMessage FromHeader(string text) {
			NameValueCollection header = ParseMailHeader(text);
			MailMessage m = new MailMessage();
			foreach (string key in header) {
				string value = header.GetValues(key)[0];
				try {
						m.Headers.Add(key, value);
				} catch {
					// HeaderCollection throws an exception if adding an empty string as
					// value, which can happen, if reading a mail message with an empty
					// subject.
					// Also spammers often forge headers, so just fall through and ignore.
				}
			}
			Match ma = Regex.Match(header["Subject"] ?? "", @"=\?([A-Za-z0-9\-]+)");
			if (ma.Success) {
				/* encoded-word subject */
				m.SubjectEncoding = Util.GetEncoding(ma.Groups[1].Value);
				m.Subject = Util.DecodeWords(header["Subject"]);
			} else {
				m.SubjectEncoding = Encoding.ASCII;
				m.Subject = header["Subject"];
			}
			m.Priority = ParsePriority(header["Priority"]);
			SetAddressFields(m, header);
			return m;
		}

		/// <summary>
		/// Parses the mail header of a mail message and returns it as a
		/// NameValueCollection.
		/// </summary>
		/// <param name="header">The mail header to parse.</param>
		/// <returns>A NameValueCollection containing the header fields as keys
		/// with their respective values as values.</returns>
		internal static NameValueCollection ParseMailHeader(string header) {
			StringReader reader = new StringReader(header);
			NameValueCollection coll = new NameValueCollection();
			string line, fieldname = null, fieldvalue = null;
			while ((line = reader.ReadLine()) != null) {
				if (line == String.Empty)
					continue;
				/* Values may stretch over several lines */
				if (line[0] == ' ' || line[0] == '\t') {
					if(fieldname != null)
						coll[fieldname] = coll[fieldname] + line.Substring(1).TrimEnd();
					continue;
				}
				/* The mail header consists of field:value pairs */
				int delimiter = line.IndexOf(':');
				if (delimiter < 0)
					continue;
				fieldname = line.Substring(0, delimiter).Trim();
				fieldvalue = line.Substring(delimiter + 1).Trim();
				coll.Add(fieldname, fieldvalue);
			}
			return coll;
		}

		/// <summary>
		/// Parses a MIME header field which can contain multiple 'parameter = value'
		/// pairs (such as Content-Type: text/html; charset=iso-8859-1).
		/// </summary>
		/// <param name="field">The header field to parse</param>
		/// <returns>A NameValueCollection containing the parameter names as keys
		/// with the respective parameter values as values.</returns>
		/// <remarks>The value of the actual field disregarding the 'parameter = value'
		/// pairs is stored in the collection under the key "value" (in the above example
		/// of Content-Type, this would be "text/html").</remarks>
		private static NameValueCollection ParseMIMEField(string field) {
			NameValueCollection coll = new NameValueCollection();
			MatchCollection matches = Regex.Matches(field, @"([\w\-]+)=\W*([\w\-\/\.]+)");
			foreach (Match m in matches)
				coll.Add(m.Groups[1].Value, m.Groups[2].Value);
			Match mvalue = Regex.Match(field, @"^\s*([\w\/]+)");
			coll.Add("value", mvalue.Success ? mvalue.Groups[1].Value : "");
			return coll;
		}

		/// <summary>
		/// Parses a mail header address-list field such as To, Cc and Bcc which
		/// can contain multiple email addresses.
		/// </summary>
		/// <param name="list">The address-list field to parse</param>
		/// <returns>An array of MailAddress objects representing the parsed
		/// mail addresses.</returns>
		private static MailAddress[] ParseAddressList(string list) {
			List<MailAddress> mails = new List<MailAddress>();
			string[] addr = list.Split(',');
			foreach (string a in addr) {
				Match m = Regex.Match(a.Trim(),
					@"(.*)\s*<?([A-Z0-9._%-]+@[A-Z0-9.-]+\.[A-Z]{2,4})>?",
					RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
				if (m.Success)
					mails.Add(new MailAddress(m.Groups[2].Value, m.Groups[1].Value));
			}
			return mails.ToArray();
		}

		/// <summary>
		/// Parses a mail message identifier from a string.
		/// </summary>
		/// <param name="field">The field to parse the message id from</param>
		/// <exception cref="ArgumentException">Thrown when the field
		/// argument does not contain a valid message identifier.</exception>
		/// <returns>The parsed message id</returns>
		/// <remarks>A message identifier (msg-id) is a globally unique
		/// identifier for a message.</remarks>
		private static string ParseMessageId(string field) {
			/* a msg-id is enclosed in < > brackets */
			Match m = Regex.Match(field, @"<(.+)>");
			if (m.Success)
				return m.Groups[1].Value;
			throw new ArgumentException("The field does not contain a valid message " +
				"identifier: " + field);
		}

		/// <summary>
		/// Parses the priority of a mail message which can be specified
		/// as part of the header information.
		/// </summary>
		/// <param name="priority">The mail header priority value. The value
		/// can be null in which case a "normal priority" is returned.</param>
		/// <returns>A value from the MailPriority enumeration corresponding to
		/// the specified mail priority. If the passed priority value is null
		/// or invalid, a normal priority is assumed and MailPriority.Normal
		/// is returned.</returns>
		private static MailPriority ParsePriority(string priority) {
			Dictionary<string, MailPriority> Map =
				new Dictionary<string, MailPriority>(StringComparer.OrdinalIgnoreCase) {
						{ "non-urgent", MailPriority.Low },
						{ "normal",	MailPriority.Normal },
						{ "urgent",	MailPriority.High }
				};
			try {
				return Map[priority];
			} catch {
				return MailPriority.Normal;
			}
		}

		/// <summary>
		/// Sets the address fields (From, To, CC, etc.) of a MailMessage
		/// object using the specified mail message header information.
		/// </summary>
		/// <param name="m">The MailMessage instance to operate on</param>
		/// <param name="header">A collection of mail and MIME headers</param>
		private static void SetAddressFields(MailMessage m, NameValueCollection header) {
			MailAddress[] addr;
			if (header["To"] != null) {
				addr = ParseAddressList(header["To"]);
				foreach (MailAddress a in addr)
					m.To.Add(a);
			}
			if (header["Cc"] != null) {
				addr = ParseAddressList(header["Cc"]);
				foreach (MailAddress a in addr)
					m.CC.Add(a);
			}
			if (header["Bcc"] != null) {
				addr = ParseAddressList(header["Bcc"]);
				foreach (MailAddress a in addr)
					m.Bcc.Add(a);
			}
			if (header["From"] != null) {
				addr = ParseAddressList(header["From"]);
				if(addr.Length > 0)
					m.From = addr[0];
			}
			if (header["Sender"] != null) {
				addr = ParseAddressList(header["Sender"]);
				if(addr.Length > 0)
					m.Sender = addr[0];
			}
			if (header["Reply-to"] != null) {
				addr = ParseAddressList(header["Reply-to"]);
				foreach (MailAddress a in addr)
					m.ReplyToList.Add(a);
			}
		}

		/// <summary>
		/// Adds a body part to an existing MailMessage instance.
		/// </summary>
		/// <param name="message">Extension method for the MailMessage class.</param>
		/// <param name="part">The body part to add to the MailMessage instance.</param>
		/// <param name="content">The content of the body part.</param>
		internal static void AddBodypart(this MailMessage message, Bodypart part, string content) {
			Encoding encoding = part.Parameters.ContainsKey("Charset") ?
				Util.GetEncoding(part.Parameters["Charset"]) : Encoding.ASCII;
			// decode content if it was encoded
			byte[] bytes;
			switch (part.Encoding) {
				case ContentTransferEncoding.QuotedPrintable:
					bytes = encoding.GetBytes(Util.QPDecode(content, encoding));
					break;
				case ContentTransferEncoding.Base64:
					try {
						bytes = Util.Base64Decode(content);
					} catch {
						// If it's not a valid Base64 string just leave the data as is
						bytes = Encoding.ASCII.GetBytes(content);
					}
					break;
				default:
					bytes = Encoding.ASCII.GetBytes(content);
					break;
			}

			// If the MIME part contains text and the MailMessage's Body fields haven't been
			// initialized yet, put it there.
			if (message.Body == string.Empty && part.Type == ContentType.Text) {
				message.Body = encoding.GetString(bytes);
				message.BodyEncoding = encoding;
				message.IsBodyHtml = part.Subtype.ToLower() == "html";
				return;
			}

			if (part.Disposition.Type == ContentDispositionType.Attachment)
				message.Attachments.Add(CreateAttachment(part, bytes));
			else
				message.AlternateViews.Add(CreateAlternateView(part, bytes));
		}

		/// <summary>
		/// Creates an instance of the Attachment class used by the MailMessage class
		/// to store mail message attachments.
		/// </summary>
		/// <param name="part">The MIME body part to create the attachment from.</param>
		/// <param name="bytes">An array of bytes composing the content of the
		/// attachment</param>
		/// <returns>An initialized instance of the Attachment class</returns>
		private static Attachment CreateAttachment(Bodypart part, byte[] bytes) {
			MemoryStream stream = new MemoryStream(bytes);
			string name = part.Disposition.Filename ?? Path.GetRandomFileName();
			Attachment attachment = new Attachment(stream, name);
			try {
				attachment.ContentId = ParseMessageId(part.Id);
			} catch {}
			string contentType = part.Type.ToString().ToLower() + "/" +
				part.Subtype.ToLower();
			attachment.ContentType = new System.Net.Mime.ContentType(contentType);
			return attachment;
		}

		/// <summary>
		/// Creates an instance of the AlternateView class used by the MailMessage class
		/// to store alternate views of the mail message's content.
		/// </summary>
		/// <param name="part">The MIME body part to create the alternate view from.</param>
		/// <param name="bytes">An array of bytes composing the content of the
		/// alternate view</param>
		/// <returns>An initialized instance of the AlternateView class</returns>
		private static AlternateView CreateAlternateView(Bodypart part, byte[] bytes) {
			MemoryStream stream = new MemoryStream(bytes);
			string contentType = part.Type.ToString().ToLower() + "/" +
				part.Subtype.ToLower();
			AlternateView view = new AlternateView(stream,
				new System.Net.Mime.ContentType(contentType));
			try {
				view.ContentId = ParseMessageId(part.Id);
			} catch {}
			return view;
		}
	}
}
