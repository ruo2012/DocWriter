﻿//
// DocConverter.cs: Routines to turn ECMA XML into an HTML string and this subset of HTML back into ECMA XML
//
// Author:
//   Miguel de Icaza (miguel@xamarin.com)
//
// Copyright 2014 Xamarin Inc
//

using System;
using System.Linq;
using System.Xml.Linq;
using System.Xml;
using System.Xml.XPath;
using System.Text;
using System.Collections.Generic;
using System.IO;
using HtmlAgilityPack;
using System.Web;

public class UnsupportedElementException : Exception {
	public UnsupportedElementException (string message) : base (message) {}
}

public static class DocConverter {

	/// <summary>
	/// Converts an HTML string that was previously generated by DocConverter to the equivalent
	/// ECMA XML node
	/// </summary>
	/// <returns>The xml.</returns>
	/// <param name="htmlstr">Htmlstr.</param>
	/// <remarks>
	/// This method might throw an UnsupportedElementException if the string is illformed, so you should protect
	/// its invocation, in particular, after the user might have edited this in a contenteditable
	/// that might have gotten elements that are not handled.
	/// </remarks>
	public static XNode [] ToXml (string htmlstr, bool canonical = false)
	{
		var doc = new HtmlDocument ();
		doc.LoadHtml (htmlstr);
		var ret = XmlToEcma.ParseRoot (doc.DocumentNode).ToArray ();

		if (canonical)
			return XmlToEcma.Canonicalize (ret);
		else
			return ret;
	}


	/// <summary>
	/// Converts an XML node that contains ECMADoc XML documentation into an HTML string that
	/// is suitable to be roundtripped back to HTML.
	/// </summary>
	/// <returns>HTML structured with annotations.  Suitable to use on a contenteditable div</returns>
	/// <param name="rootNode">Root node containing the ECMA documentation, the containing node.</param>
	/// <param name="currentFile">Message hint used when reporting warnings.</param>
	public static string ToHtml (XElement rootNode, string currentFile)
	{
		var ret = new EcmaToXml (currentFile).Convert (rootNode);
		return ret;
	}
}

static class XmlToEcma {
	public static IEnumerable<XNode> ParseRoot (HtmlNode node)
	{
		foreach (var element in node.ChildNodes){
			switch (element.Name) {
			case "p":
				if (element.OuterHtml == "<p/>")
					yield return new XElement ("para");
				else
					yield return ParseP (element);
				break;
			case "div":
				if (element.Attributes ["class"] != null) {
					foreach (var div in ParseDiv (element))
						yield return div;
				} else
					yield return ParseP (element);
				break;
			case "table":
				yield return ParseTable (element);
				break;
			case "ul":
			case "ol":
				yield return ParseList (element, element.Name == "ul" ? "bullet" : "number");
				break;
			case "a":
				yield return new XElement ("see", new XAttribute ("cref", element.InnerText));
				break;
			case "code":
				var kind = element.Attributes ["class"].Value;
				switch (kind) {
				case "code":
					var c = new XElement ("c");
					foreach (var n in ParseRoot (element))
						c.Add (n);
					yield return c;
					break;
				case "langword":
					yield return new XElement ("see", new XAttribute ("langword", element.InnerText));
					break;
				case "paramref":
					yield return new XElement ("paramref", new XAttribute ("name", element.InnerText));
					break;
				case "typeparamref":
					yield return new XElement ("typeparamref", new XAttribute ("name", element.InnerText));
					break;
				default:
					throw new UnsupportedElementException ("Do not know how to handle <code class='" + kind);
				}
				break;
			case "img":
				yield return new XElement ("img", new XAttribute ("href", element.Attributes ["src"].Value));
				break;
			case "br":
				yield return new XElement ("para");
				break;
			default:
				if (element is HtmlTextNode) {
					yield return new XText (HttpUtility.HtmlDecode ((element as HtmlTextNode).Text));
				} else if (element is HtmlCommentNode) {
					yield return new XComment ((element as HtmlCommentNode).Comment);
				} else
					throw new UnsupportedElementException ("Do not have support for " + element.Name);
				break;
			}
		}
	}

	static bool IsInlineElement (XNode node)
	{
		if (node is XText || node is XComment || node is XCData)
			return true;
		if (node is XElement) {
			switch (((XElement)node).Name.ToString ()) {
			case "para":
			case "code":
			case "list":
			case "format":
			case "example":
			case "related":
			case "block":
				return false;
			case "see":
			case "img":
			case "paramref":
			case "typeparamref":
			case "c":
				return true;
			}
		}
		throw new NotImplementedException ("Do not know if this kind of element can be inlined: " + node.GetType ().ToString ());
	}

	//
	// This turns XML that might have text + toplevel containers into
	// a <para> with the toplevel text + the rest of the containers
	//
	// This turns the invalid "foo<para>bar</para>" into "<para>foo</para><para>bar</para>
	//
	public static XNode [] Canonicalize (XNode [] nodes)
	{
		if (nodes.Length > 0 && nodes [0] is XElement && (nodes [0] as XElement).Name == "para")
			return nodes;

		bool inline = false;
		for (int i = 0; i < nodes.Length; i++){
			if (IsInlineElement (nodes [i])) {
				inline = true;
			} else {
				if (inline) {
					var result = new List<XNode> ();
					var container = new XElement  ("para");
					int j = 0;
					for (; j < i; j++)
						container.Add (nodes [j]);
					result.Add (container);
					for (; j < nodes.Length; j++){
						result.Add (nodes [j]);
					}
					return result.ToArray ();
				}
			}
		}
		return nodes;
	}

	static XElement ParseList (HtmlNode node, string kind)
	{
		var list = new XElement ("list", new XAttribute ("type", kind));

		foreach (var li in node.Elements ("li")) {
			list.Add (new XElement ("item", new XElement ("term", ParseRoot (li))));
		}
		return list;
	}

	static XElement ParseTable (HtmlNode node)
	{
		var lookupNode = node.ChildNodes ["tbody"];
		if (lookupNode == null)
			lookupNode = node;

		var ftr = lookupNode.ChildNodes ["tr"];
		var nodes = ftr.SelectNodes ("th");
		var term = new XElement ("term", ParseRoot (nodes [0]));
		var header = new XElement ("listheader", term);
		foreach (var desc in nodes.Skip (1))
			header.Add (new XElement ("description", ParseRoot (desc)));

		var list = new XElement ("list", new XAttribute ("type", "table"), header);

		int tr = 0;
		foreach (var child in lookupNode.SelectNodes ("tr").Skip (1)){

			var tds = child.SelectNodes ("td");
			term = new XElement ("term", ParseRoot (tds [0]));
			var item = new XElement ("item", term);

			foreach (var desc in tds.Skip (1))
				item.Add (new XElement ("description", ParseRoot (desc)));

			list.Add (item);
		}

		return list;
	}

	static XElement ParseP (HtmlNode node)
	{
		var xp = new XElement ("para");

		// tool=nullallowed -> tool=nullallowed
		// id=tool-remark -> tool=remark
		var tool = node.Attributes ["tool"];
		if (tool != null) {
			XAttribute a;

			var v = tool.Value;
			if (v == "tool-remark")
				a = new XAttribute ("id", "tool-remark");
			else
				a = new XAttribute ("tool", tool.Value);

			xp.Add (a);
		}
		var copied = node.Attributes ["copied"];
		if (copied != null) 
			xp.Add (new XAttribute ("copied", "true"));


		if (node.ChildNodes.Count == 0)
			xp.SetValue (string.Empty);
		foreach (var child in node.ChildNodes) {
			if (child is HtmlTextNode) {
				var tn = child as HtmlTextNode;
				xp.Add (new XText (HttpUtility.HtmlDecode (tn.Text)));
			} else if (child is HtmlNode) {
				var childName = child.Name;

				switch (child.Name) {
				case "a":
					xp.Add (new XElement ("see", new XAttribute ("cref", child.InnerText)));
					break;
				case "img":
					xp.Add (new XElement ("img", new XAttribute ("href", child.Attributes ["src"].Value)));
					break;
				case "code":
					var kind = child.Attributes ["class"].Value;
					switch (kind) {
					case "code":
						var c = new XElement ("c");
						foreach (var n in ParseRoot (child))
							c.Add (n);
						xp.Add (c);
						break;
					case "langword":
						xp.Add (new XElement ("see", new XAttribute ("langword", child.InnerHtml)));
						break;
					case "paramref":
						xp.Add (new XElement ("paramref", new XAttribute ("name", child.InnerHtml)));
						break;
					case "typeparamref":
						xp.Add (new XElement ("typeparamref", new XAttribute ("name", child.InnerHtml)));
						break;
					default:
						throw new UnsupportedElementException ("Do not know how to handle <code class='" + kind + "'>");
					}
					break;
				case "div":
					foreach (var divNode in ParseDiv (child))
						xp.Add (divNode);
					break;
				case "table":
					xp.Add (ParseTable (child));
					break;
				case "br":
					break;
				case "ul":
				case "ol":
					xp.Add (ParseList (child, childName == "ul" ? "bullet" : "number"));
					break;
				default:
					throw new UnsupportedElementException ("Do not know how to handle <" + child.Name + "> inside a <p>");
				}
			}
		}
		return xp;
	}

	static void Verbatim (XElement target, HtmlNodeCollection nodes)
	{
		foreach (var n in nodes) {
			if (n is HtmlTextNode)
				target.Add (new XText ((n as HtmlTextNode).Text));
			else {
				var nt = new XElement (n.Name);
				foreach (var a in n.Attributes) {
					nt.Add (new XAttribute (a.Name, a.Value));
				}
				Verbatim (nt, n.ChildNodes);
				target.Add (nt);
			}
		}
	}

	static IEnumerable<XNode> ParseDiv (HtmlNode node)
	{
		var classAttr = node.Attributes ["class"];
		var dclass = classAttr == null ? "" : classAttr.Value;

		if (dclass.StartsWith ("lang-")) {
			var code = new XElement ("code", new XAttribute ("lang", dclass.Substring (5).Replace ("sharp", "#")));

			var cdata = node.Attributes ["data-cdata"];
			if (cdata != null) {
				code.Add (new XCData (HttpUtility.HtmlDecode (node.FirstChild.InnerText)));
			} else {
				if (node.FirstChild != null)
					code.Add (new XText (HttpUtility.HtmlDecode (node.FirstChild.InnerHtml)));
			}
			yield return code;
		} else if (dclass == "example") {
			var example = new XElement ("example");
			foreach (var child in node.ChildNodes) {
				if (child is HtmlTextNode)
					example.Add (new XText (HttpUtility.HtmlDecode ((child as HtmlTextNode).Text)));
				else if (child.Name == "div") {
					var _edclass = child.Attributes ["class"];
					var edclass = _edclass == null ? null : _edclass.Value;
					if (edclass != null && !(edclass.StartsWith ("lang-") || edclass.IndexOf ("example-body") != -1 || edclass.IndexOf ("example-title") != -1))
						throw new UnsupportedElementException ("Do not know how to handle a div whose class does not start with lang- inside an <div class='example'>");
					if (edclass.IndexOf ("skip") == -1) {
						if (edclass == "example-body") {
							foreach (var ebchild in child.ChildNodes) {
								example.Add (ParseDiv (ebchild));
							} 
						} else if (edclass == "codeblock") {
							foreach (var cc in child.ChildNodes)
								example.Add (ParseDiv (cc));
						} else
							example.Add (ParseDiv (child));
					}
				} else
					throw new UnsupportedElementException ("Do not know how to handled non-div elements inside <example>");
			}
			yield return example;
		} else if (dclass == "verbatim") {
			var format = new XElement ("format", new XAttribute ("type", "text/html"));
			Verbatim (format, node.ChildNodes);
			yield return format;
		} else if (dclass.StartsWith ("block")) {
			var kind = dclass.Substring (6);
			var block = new XElement ("block", new XAttribute ("subset", "none"), new XAttribute ("type", kind));
			foreach (var child in ParseRoot (node))
				block.Add (child);
			yield return block;
		} else if (dclass == "related") {
			var link = node.ChildNodes ["a"];
			if (link != null) {
				var href = link.Attributes ["href"].Value;
				var type = link.Attributes ["data-type"].Value;
				var text = (link.FirstChild as HtmlTextNode).Text;
				// this could be more robust, perhaps we should put the type not inside the child, but as an attribute in the <a>

				var related = new XElement ("related", new XAttribute ("type", type), new XAttribute ("href", href));
				if (text != null && text != "")
					related.Add (new XText (HttpUtility.HtmlDecode (text)));
				yield return related;
			}
		} else if (dclass == "cdata") {
			yield return new XCData (HttpUtility.HtmlDecode (node.InnerText));
		} else if (dclass.StartsWith ("skip ") || dclass == "skip") {
			// nothing, ignore
		} else if (dclass == "codeblock" || dclass == "") {
			var parsed = new List<XNode> ();
			foreach (var ncn in node.ChildNodes) {
				foreach (var r in ParseDiv (ncn))
					parsed.Add (r);
			}
			var ret = new List<XNode> ();
			XElement last = null;
			for (int i = 0; i < parsed.Count; i++) {
				var n = parsed [i];
				var ne = n as XElement;
				if (ne != null) {
					if (last != null) {
						last.Add (new XText ("\n"));
						last.Add (ne.FirstNode);
					} else {
						last = ne;
						ret.Add (ne);
					}
				} else {
					ret.Add (n);
				}
			}
			foreach (var e in ret)
				yield return e;
		} else
			throw new UnsupportedElementException ("Unknown div style: " + dclass);
	}

}

class EcmaToXml {
	string currentFile;

	public EcmaToXml (string currentFile = null)
	{
		if (currentFile == null)
			currentFile = "<In memory>";

		this.currentFile = currentFile;
	}

	int warnCount = 0;
	void WarningDangling (XElement root, XNode node)
	{
		Console.WriteLine ("Warning {2}, dangling text at {1}\n{3} =>\n {0}", currentFile, root, warnCount++, node);
	}


	public string Convert (XElement root)
	{
		var sb = new StringBuilder ();
		bool seenPara = false;
		bool first = true;
		bool renderedText = false;

		foreach (var node in root.Nodes ()) {
			if (node is XText){
				if (first) {
					if (root.IsEmpty)
						sb.Append ("<p/>");
					else {
						// Should we really RenderPara here?   Should we not just add the text, and continue?
						// The warning should just be about a para started if we had text initially:
						// IMPORTANT: If you uncomment the next line, change the "continue" for a "break"
						//sb.AppendFormat ("{0}", RenderPara (root.Nodes ()));
						sb.Append (EncodeText (node as XText));
					}
					renderedText = true;
				} else if (seenPara && currentFile.IndexOf ("MonoTouch.Dialog") == -1)
					WarningDangling (root, node);
				else
					sb.Append (EncodeText (node as XText));
				continue; // break
			}
			first = false;

			if (node is XElement) {
				var el = node as XElement;

				switch (el.Name.ToString ()) {
				case "para":
					seenPara = true;
					if (renderedText) 
						WarningDangling (root, node);
					string attr = "";
					var toola = el.Attribute ("tool");
					if (toola != null) {
						attr = " tool='" + toola.Value + "'";
					} 
					var id = el.Attribute ("id");
					if (id != null) {
						attr = " tool='tool-remark'";
					}
					var copied = el.Attribute ("copied");
					if (copied != null) {
						attr += " copied='true'";
					}
					if (el.IsEmpty)
						sb.AppendFormat ("<p{0}/>", attr);
					else
						sb.AppendFormat ("<p{0}>{1}</p>", attr, RenderPara (el.Nodes ()));
					break;
				case "list":
					sb.Append (RenderList (el));
					break;
				case "code":
					sb.Append (RenderCode (el));
					break;
				case "format":
					sb.Append (RenderFormat (el));
					break;
				case "example":
					sb.Append (RenderExample (el));
					break;
				case "see":
					sb.Append (RenderSee (el));
					break;
				case "block":
					sb.Append (RenderBlock (el));
					break;
				case "related":
					sb.Append (RenderRelated (el));
					break;
				case "img":
					sb.Append (RenderImage (el));
					break;
				case "paramref":
					sb.Append (RenderParamRef (el));
					break;
				case "typeparamref":
					sb.Append (RenderTypeParamRef (el));
					break;
				case "c":
					sb.Append (RenderC (el));
					break;
				default:
					Console.WriteLine ("File: {0} node: {1}", currentFile, el);
					throw new UnsupportedElementException ("No support for handling nodes of type " + el.Name);
				}
			} else if (node is XComment) {
				var xc = node as XComment;
				sb.AppendFormat ("<!--{0}-->", xc.Value);
			} else
				throw new UnsupportedElementException ("Do not know how to handle " + node.GetType ());
		}
		return sb.ToString ();
	}

	string EncodeText (XText text)
	{
		if (text.NodeType == XmlNodeType.CDATA)
			return string.Format ("<div class='cdata'>{0}</div>", HttpUtility.HtmlEncode (text.Value));
		else
			return HttpUtility.HtmlEncode (text.Value);
	}

	string RenderRelated (XElement el)
	{
		var type = el.Attribute ("type").Value;
		var href = el.Attribute ("href").Value;
		return string.Format ("<div class='related'>Related: <a data-type='{0}' href='{1}'>{2}</a></div>", type, href, HttpUtility.HtmlEncode (el.Value));
	}

	string RenderList (XElement el)
	{
		var sb = new StringBuilder ();
		var kind = el.Attribute ("type").Value;
		switch (kind) {
		case "bullet":
			sb.AppendFormat ("<ul>{0}</ul>", RenderListBullet (el.Elements ()));
			break;
		case "number":
			sb.AppendFormat ("<ol>{0}</ol>", RenderListBullet (el.Elements ()));
			break;
		case "table":
			sb.AppendFormat ("<table style='width:100%; table-layout: fixed; border-collapse: collapse;'>\n{0}\n{1}\n</table>", 
				RenderTableElement ("th", el.Element ("listheader")), 
				string.Join ("\n", el.Elements ("item").Select (x => RenderTableElement ("td", x)).ToArray ()));
			break;
		default:
			throw new UnsupportedElementException ("list type not supported: " + kind);
		}
		return sb.ToString ();
	}

	// Renders a table element, the kind is used to render th or td
	// <term>..</term><description>..</description>+
	string RenderTableElement (string kind, XElement top)
	{
		var sb = new StringBuilder ();
		sb.AppendFormat ("<tr>\n  <{0}>{1}</{0}>", kind, Convert (top.Element ("term")));

		foreach (var desc in top.Elements ("description"))
			sb.AppendFormat ("  <{0}>{1}</{0}>", kind, Convert (desc));
		sb.Append ("</tr>");
		return sb.ToString ();
	}

	// Renders <format>..</format>
	string RenderFormat (XElement el)
	{
		var kind = el.Attribute ("type").Value;
		if (kind == "text/html") {
			return string.Format ("<div class='verbatim'>{0}</div>", Verbatim (el.Nodes ()));
		} else
			throw new UnsupportedElementException ("Do not support anything but <format type='text/html'>");
	}

	// Renders <example>...</example>
	string RenderExample (XElement el)
	{
		return string.Format ("<div class='example'><div class='skip example-title' contenteditable='false'>Example</div><div class='example-body'>{0}</div></div>", RenderPara (el.Nodes ()));
	}

	string RenderBlock (XElement el)
	{
		var attr = el.Attribute ("type").Value;
		return string.Format ("<div class='block {0}'>{1}</div>", attr, Convert (el));
	}


	// REnders a <c>...</c> optionally with a CData
	string RenderC (XElement el)
	{
		return string.Format ("<code class='code'>{0}</code>", Convert (el));
	}

	// Renders <para>...</para>
	string RenderPara (IEnumerable<XNode> nodes)
	{
		var sb = new StringBuilder ();
		foreach (var node in nodes) {
			if (node is XText)
				sb.Append (HttpUtility.HtmlEncode ((node as XText).Value));
			else if (node is XElement) {
				var xel = node as XElement;
				if (xel.Name == "see") {
					sb.Append (RenderSee (xel));
				} else if (xel.Name == "img") {
					sb.Append (RenderImage (xel));
				} else if (xel.Name == "c") {
					sb.Append (RenderC (xel));
				} else if (xel.Name == "format") {
					sb.Append (RenderFormat (xel));
				} else if (xel.Name == "list") {
					sb.AppendFormat ("{0}", RenderList (xel));
				} else if (xel.Name == "paramref") {
					sb.Append (RenderParamRef (xel));
				} else if (xel.Name == "typeparamref") {
					sb.Append (RenderTypeParamRef (xel));
				} else if (xel.Name == "example") {
					Console.WriteLine ("EXAMPLE at {0}", currentFile);
				} else if (xel.Name == "code") {
					sb.Append (RenderCode (xel));
				} else {
					Console.WriteLine ("File: {0}, Node: {1}", currentFile, node);
					throw new UnsupportedElementException ("Unsupported element in RenderPara: " + xel.Name);
				}
			} else {
				throw new NotSupportedException ("Unknown node type: " + node.GetType ());
			}
		}
		return sb.ToString ();
	}

	string RenderImage (XElement xel)
	{
		var target = xel.Attribute ("href").Value;
		return string.Format ("<img src='{0}'>", target);
	}

	string RenderParamRef (XElement xel)
	{
		return string.Format ("<code class='paramref'>{0}</code>", xel.Attribute ("name").Value);
	}

	string RenderTypeParamRef (XElement xel)
	{
		return string.Format ("<code class='typeparamref'>{0}</code>", xel.Attribute ("name").Value);
	}

	string RenderSee (XElement xel)
	{
		var target = xel.Attribute ("cref");
		if (target != null)
			return string.Format ("<a href=''>{0}</a>", target.Value);
		var lang = xel.Attribute ("langword").Value;
		return string.Format ("<code class='langword'>{0}</code>", lang);
	}

	string RenderListBullet (IEnumerable<XElement> elements)
	{
		var sb = new StringBuilder ();
		foreach (var xel in elements) {
			if (xel.HasAttributes)
				throw new UnsupportedElementException ("Do not support attributes on list[bullet]/items");
			var children = xel.Elements ();
			if (children.Count () > 1) 
				throw new UnsupportedElementException ("Do not support more than one item inside list[bullet]/item");
			var first = children.FirstOrDefault ();
			if (first == null)
				sb.Append ("<li></li>");
			else if (first.Name == "term")
				sb.AppendFormat ("<li>{0}</li>", Convert (first));
			else
				throw new UnsupportedElementException ("Do not support anything but a term inside a list[bullet]/item");
		}
		return sb.ToString ();
	}

	string RenderExample (IEnumerable<XElement> elements)
	{
		var sb = new StringBuilder ();
		foreach (var c in elements) {
			if (c.Name == "code") {
				sb.Append (RenderCode (c));
			} else
				throw new UnsupportedElementException ("Do not know how to handle inside an example a node of type " + c.Name);
		}
		return sb.ToString ();
	}

	string RenderCode (XElement code)
	{
		var blang = code.Attribute ("lang").Value;
		var lang = blang.Replace ("#", "sharp");
		if (lang == "")
			throw new UnsupportedElementException ("No language specified for <code> tag inside example");
		int count = code.Nodes ().Count ();

		if (count > 1) {
			Console.WriteLine ("NODES: {0}", code.Nodes ().Count ());
		}
		string value;
		var cdata = "";
		if (count > 0) {
			var child = code.FirstNode;

			if (child is XCData) {
				cdata = " data-cdata='true'";
				value = HttpUtility.HtmlEncode ((child as XCData).Value);
			} else {
				var sb = new StringBuilder ();
				foreach (var c in code.Nodes ()) {
					if (c is XText) {
						sb.Append ((c as XText).Value);
					}
				}
				value = sb.ToString ();
			}
		} else
			value = "";
		return "<div class='codeblock'><div class='skip code-label' contenteditable='false'>// Language " + blang + "</div><div class='lang-" + lang + "'" + cdata + ">" + value + "</div></div>";
	}

	string Verbatim (IEnumerable<XNode> nodes)
	{
		return String.Join (" ", nodes.Select (x => x.ToString ()).ToArray ());
	}
}
