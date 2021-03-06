﻿//
// ElementParser.cs
//
// Ported to C# from project Chameleon.
// Original source code: https://github.com/malthe/chameleon/blob/master/src/chameleon/parser.py
//
// Author:
//   Roman Lacko (backup.rlacko@gmail.com)
//
// Copyright (c) 2010 - 2013 Roman Lacko
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpTAL.TemplateParser
{
	/// <summary>
	/// Parses tokens into elements.
	/// </summary>
	public class ElementParser
	{
		static readonly string XML_NS = "http://www.w3.org/XML/1998/namespace";

		static readonly Regex match_tag_prefix_and_name = new Regex(
			@"^(?<prefix></?)(?<name>([^:\n ]+:)?[^ \r\n\t>/]+)(?<suffix>(?<space>\s*)/?>)?", RegexOptions.Singleline);
		static readonly Regex match_single_attribute = new Regex(
			@"(?<space>\s+)(?!\d)" +
			@"(?<name>[^ =/>\n\t]+)" +
			@"((?<eq>\s*=\s*)" +
			@"((?<quote>[\'""])(?<value>.*?)\k<quote>|" +
			@"(?<alt_value>[^\s\'"">/]+))|" +
			@"(?<simple_value>(?![ \\n\\t\\r]*=)))", RegexOptions.Singleline);
		static readonly Regex match_comment = new Regex(
			@"^<!--(?<text>.*)-->$", RegexOptions.Singleline);
		static readonly Regex match_cdata = new Regex(
			@"^<!\[CDATA\[(?<text>.*)\]>$", RegexOptions.Singleline);
		static readonly Regex match_declaration = new Regex(
			@"^<!(?<text>[^>]+)>$", RegexOptions.Singleline);
		static readonly Regex match_processing_instruction = new Regex(
			//@"^<\?(?<text>.*?)\?>", RegexOptions.Singleline);
			@"^<\?(?<name>\w+)(?<text>.*?)\?>", RegexOptions.Singleline);
		static readonly Regex match_xml_declaration = new Regex(
			@"^<\?xml(?=[ /])", RegexOptions.Singleline);

		IEnumerable<Token> stream;
		List<Dictionary<string, string>> namespaces;
		List<Element> queue;
		Stack<KeyValuePair<Token, int>> index;

		public ElementParser(IEnumerable<Token> stream, Dictionary<string, string> defaultNamespaces)
		{
			this.stream = stream;
			this.queue = new List<Element>();
			this.index = new Stack<KeyValuePair<Token, int>>();
			this.namespaces = new List<Dictionary<string, string>> { new Dictionary<string, string>(defaultNamespaces) };
		}

		public IEnumerable<Element> Parse()
		{
			foreach (var token in this.stream)
			{
				var item = this.ParseToken(token);
				this.queue.Add(item);
			}
			return this.queue;
		}

		Element ParseToken(Token token)
		{
			TokenKind kind = token.Kind;
			if (kind == TokenKind.Comment)
				return visit_comment(token);
			if (kind == TokenKind.CData)
				return visit_cdata(token);
			if (kind == TokenKind.ProcessingInstruction)
				return visit_processing_instruction(token);
			if (kind == TokenKind.EndTag)
				return visit_end_tag(token);
			if (kind == TokenKind.EmptyTag)
				return visit_empty_tag(token);
			if (kind == TokenKind.StartTag)
				return visit_start_tag(token);
			if (kind == TokenKind.Text)
				return visit_text(token);
			return visit_default(token);
		}

		Element visit_comment(Token token)
		{
			return new Element(ElementKind.Comment, token);
		}

		Element visit_default(Token token)
		{
			return new Element(ElementKind.Default, token);
		}

		Element visit_text(Token token)
		{
			return new Element(ElementKind.Text, token);
		}

		Element visit_cdata(Token token)
		{
			return new Element(ElementKind.CData, token);
		}

		Element visit_processing_instruction(Token token)
		{
			Match match = match_processing_instruction.Match(token.ToString());
			Dictionary<string, object> node = groupdict(match_processing_instruction, match, token);
			return new Element(ElementKind.ProcessingInstruction, node);
		}

		Element visit_start_tag(Token token)
		{
			var ns = new Dictionary<string, string>(namespaces.Last());
			namespaces.Add(ns);
			var node = parse_tag(token, ns);
			index.Push(new KeyValuePair<Token, int>(node["name"] as Token, queue.Count));
			return new Element(ElementKind.StartTag, node);
		}

		Element visit_end_tag(Token token)
		{
			Dictionary<string, string> ns;
			try
			{
				ns = namespaces.Last();
				namespaces.RemoveAt(namespaces.Count - 1);
			}
			catch (InvalidOperationException ex)
			{
				throw new ParseError("Unexpected end tag.", token);
			}
			Dictionary<string, object> node = parse_tag(token, ns); ;
			while (index.Count > 0)
			{
				KeyValuePair<Token, int> idx = index.Pop();
				Token name = idx.Key;
				int pos = idx.Value;
				if (node["name"].Equals(name))
				{
					Element el = queue[pos];
					queue.RemoveAt(pos);
					Dictionary<string, object> start = el.StartTagTokens;
					List<Element> children = queue.GetRange(pos, queue.Count - pos);
					queue.RemoveRange(pos, queue.Count - pos);
					return new Element(ElementKind.Element, start, node, children);
				}
			}
			throw new ParseError("Unexpected end tag.", token);
		}

		Element visit_empty_tag(Token token)
		{
			var ns = new Dictionary<string, string>(namespaces.Last());
			var node = parse_tag(token, ns);
			return new Element(ElementKind.Element, node);
		}

		static Dictionary<string, object> groupdict(Regex r, Match m, Token token)
		{
			Dictionary<string, object> d = new Dictionary<string, object>();
			foreach (string name in r.GetGroupNames())
			{
				Group g = m.Groups[name];
				if (g != null)
				{
					int i = g.Index;
					int j = g.Length;
					d.Add(name, token.Substring(i, j));
				}
				else
				{
					d.Add(name, null);
				}
			}
			return d;
		}

		static Dictionary<string, object> match_tag(Token token)
		{
			Match match = match_tag_prefix_and_name.Match(token.ToString());
			Dictionary<string, object> node = groupdict(match_tag_prefix_and_name, match, token);

			int end = match.Index + match.Length;
			token = token.Substring(end);

			var attrs = new List<Dictionary<string, object>>();
			node["attrs"] = attrs;

			foreach (Match m in match_single_attribute.Matches(token.ToString()))
			{
				Dictionary<string, object> attr = groupdict(match_single_attribute, m, token);
				Token alt_value = null;
				if (attr.Keys.Contains("alt_value"))
				{
					alt_value = attr["alt_value"] as Token;
					attr.Remove("alt_value");
					if (!string.IsNullOrEmpty(alt_value.ToString()))
					{
						attr["value"] = alt_value;
						attr["quote"] = "";
					}
				}
				Token simple_value = null;
				if (attr.Keys.Contains("simple_value"))
				{
					simple_value = attr["simple_value"] as Token;
					attr.Remove("simple_value");
					if (!string.IsNullOrEmpty(simple_value.ToString()))
					{
						attr["quote"] = "";
						attr["value"] = new Token("");
						attr["eq"] = "";
					}
				}
				attrs.Add(attr);
				int m_end = m.Index + m.Length;
				node["suffix"] = token.Substring(m_end);
			}

			return node;
		}

		static Dictionary<string, object> parse_tag(Token token, Dictionary<string, string> ns)
		{
			var node = match_tag(token);

			update_namespace(node["attrs"] as List<Dictionary<string, object>>, ns);

			string prefix = null;
			if ((node["name"] as Token).ToString().Contains(':'))
				prefix = (node["name"] as Token).ToString().Split(':')[0];

			string defaultNs = prefix != null && ns.ContainsKey(prefix) ? ns[prefix] : XML_NS;
			node["namespace"] = defaultNs;
			node["ns_attrs"] = unpack_attributes(node["attrs"] as List<Dictionary<string, object>>, ns, defaultNs);

			return node;
		}

		static void update_namespace(List<Dictionary<string, object>> attributes, Dictionary<string, string> ns)
		{
			foreach (var attribute in attributes)
			{
				string name = ((Token)attribute["name"]).ToString();
				string value = ((Token)attribute["value"]).ToString();

				if (name == "xmlns")
					ns[""] = value;
				else if (name.ToString().StartsWith("xmlns:"))
					ns[name.Substring(6)] = value;
			}
		}

		static OrderedDictionary unpack_attributes(List<Dictionary<string, object>> attributes, Dictionary<string, string> ns, string defaultNs)
		{
			OrderedDictionary namespaced = new OrderedDictionary();

			foreach (var attribute in attributes)
			{
				string name = ((Token)attribute["name"]).ToString();
				string value = ((Token)attribute["value"]).ToString();

				string n = null;
				string prefix = null;
				if (name.Contains(':'))
				{
					prefix = name.Split(':')[0];
					name = name.Substring(prefix.Length + 1);
					try
					{
						n = ns[prefix];
					}
					catch (KeyNotFoundException ex)
					{
						throw new KeyNotFoundException(
							string.Format("Undefined namespace prefix: {0}.", prefix));
					}
				}
				else
					n = defaultNs;

				namespaced[new KeyValuePair<string, string>(n, name)] = value;
			}

			return namespaced;
		}
	}
}
