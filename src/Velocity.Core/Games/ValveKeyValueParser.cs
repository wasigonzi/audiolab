using System;
using System.Collections.Generic;
using System.Text;

namespace Velocity.Core.Games;

/// <summary>A node in a Valve KeyValues document: either a string or a set of child nodes.</summary>
public sealed class KeyValueNode
{
    private readonly Dictionary<string, KeyValueNode> _children =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a leaf node.</summary>
    /// <param name="value">The string value.</param>
    public KeyValueNode(string value) => Value = value;

    /// <summary>Creates a container node.</summary>
    public KeyValueNode()
    {
    }

    /// <summary>The string value, for a leaf node.</summary>
    public string? Value { get; }

    /// <summary>Child nodes, for a container node.</summary>
    public IReadOnlyDictionary<string, KeyValueNode> Children => _children;

    /// <summary>Adds or replaces a child.</summary>
    /// <param name="key">Child key.</param>
    /// <param name="node">Child node.</param>
    public void Add(string key, KeyValueNode node) => _children[key] = node;

    /// <summary>Returns a child by key, or null.</summary>
    /// <param name="key">Child key.</param>
    /// <returns>The child, or <see langword="null"/>.</returns>
    public KeyValueNode? Child(string key) =>
        _children.TryGetValue(key, out KeyValueNode? node) ? node : null;

    /// <summary>Returns a descendant's string value by walking a key path.</summary>
    /// <param name="keys">Keys to walk, in order.</param>
    /// <returns>The value, or <see langword="null"/> when any step is missing.</returns>
    public string? ValueAt(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        KeyValueNode? current = this;
        foreach (string key in keys)
        {
            current = current?.Child(key);
        }

        return current?.Value;
    }
}

/// <summary>
/// Parses Valve's text KeyValues format, used by <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format is small: quoted keys, either a quoted value or a brace delimited block, with
/// <c>//</c> line comments and backslash escapes inside quotes. Only the text form is handled; the
/// binary form does not appear in the files this product reads.
/// </para>
/// <para>
/// A malformed file returns whatever parsed before the problem rather than throwing. A game library
/// scan is a best effort convenience, and one corrupt manifest should not cost the user the rest of
/// their library.
/// </para>
/// </remarks>
public static class ValveKeyValueParser
{
    /// <summary>Parses a document.</summary>
    /// <param name="text">Document text.</param>
    /// <returns>The root container node.</returns>
    public static KeyValueNode Parse(string? text)
    {
        var root = new KeyValueNode();

        if (string.IsNullOrEmpty(text))
        {
            return root;
        }

        int position = 0;
        ParseInto(root, text, ref position, depth: 0);
        return root;
    }

    private const int MaximumDepth = 64;

    private static void ParseInto(KeyValueNode parent, string text, ref int position, int depth)
    {
        if (depth > MaximumDepth)
        {
            position = text.Length;
            return;
        }

        while (true)
        {
            SkipTrivia(text, ref position);

            if (position >= text.Length || text[position] == '}')
            {
                position = Math.Min(position + 1, text.Length);
                return;
            }

            string? key = ReadToken(text, ref position);
            if (key is null)
            {
                return;
            }

            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                return;
            }

            if (text[position] == '{')
            {
                position++;
                var child = new KeyValueNode();
                ParseInto(child, text, ref position, depth + 1);
                parent.Add(key, child);
                continue;
            }

            string? value = ReadToken(text, ref position);
            if (value is null)
            {
                return;
            }

            parent.Add(key, new KeyValueNode(value));
        }
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            char current = text[position];

            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (current == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] is not ('\n' or '\r'))
                {
                    position++;
                }

                continue;
            }

            return;
        }
    }

    private static string? ReadToken(string text, ref int position)
    {
        if (position >= text.Length)
        {
            return null;
        }

        if (text[position] != '"')
        {
            // An unquoted token, which the format permits for simple keys.
            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) &&
                   text[position] is not ('{' or '}'))
            {
                position++;
            }

            return position > start ? text[start..position] : null;
        }

        position++;
        var builder = new StringBuilder();

        while (position < text.Length)
        {
            char current = text[position++];

            if (current == '\\' && position < text.Length)
            {
                char escaped = text[position++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }

            if (current == '"')
            {
                return builder.ToString();
            }

            builder.Append(current);
        }

        // Unterminated string: return what was read rather than losing the whole file.
        return builder.ToString();
    }
}
