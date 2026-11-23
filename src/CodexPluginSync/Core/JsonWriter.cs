using System;
using System.Globalization;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// Minimal append-only JSON emitter. Compact (single line), no dependency, deterministic key order.
    /// Used for stdout --json output (§13.3: "单行或 2 空格缩进"; single line is allowed).
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(2048);
        private bool _needComma;

        public override string ToString()
        {
            return _sb.ToString();
        }

        private void Separate()
        {
            if (_needComma)
            {
                _sb.Append(',');
            }
            _needComma = false;
        }

        public JsonWriter BeginObject()
        {
            Separate();
            _sb.Append('{');
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            _needComma = true;
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separate();
            _sb.Append('[');
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            _needComma = true;
            return this;
        }

        /// <summary>Writes the key part of a member; follow with a value call or BeginObject/BeginArray.</summary>
        public JsonWriter Name(string name)
        {
            Separate();
            AppendString(name);
            _sb.Append(':');
            return this;
        }

        public JsonWriter Prop(string name, string value)
        {
            Name(name);
            return Value(value);
        }

        public JsonWriter Prop(string name, long value)
        {
            Name(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public JsonWriter Prop(string name, long? value)
        {
            Name(name);
            return Value(value);
        }

        public JsonWriter Prop(string name, int value)
        {
            return Prop(name, (long)value);
        }

        public JsonWriter Prop(string name, bool value)
        {
            Name(name);
            _sb.Append(value ? "true" : "false");
            _needComma = true;
            return this;
        }

        public JsonWriter Prop(string name, bool? value)
        {
            Name(name);
            if (!value.HasValue)
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append(value.Value ? "true" : "false");
            }
            _needComma = true;
            return this;
        }

        /// <summary>Null string member (contract requires the key to exist with null, not to be omitted).</summary>
        public JsonWriter Null(string name)
        {
            Name(name);
            _sb.Append("null");
            _needComma = true;
            return this;
        }

        /// <summary>Embeds an already-serialized JSON fragment verbatim.</summary>
        public JsonWriter PropRaw(string name, string rawJson)
        {
            Name(name);
            _sb.Append(rawJson);
            _needComma = true;
            return this;
        }

        public JsonWriter Value(string value)
        {
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                AppendString(value);
            }
            _needComma = true;
            return this;
        }

        public JsonWriter Value(long? value)
        {
            if (!value.HasValue)
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            }
            _needComma = true;
            return this;
        }

        private void AppendString(string value)
        {
            _sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u007f')
                        {
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
