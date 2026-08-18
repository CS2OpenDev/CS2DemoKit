#region

using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

// Copied verbatim from demofile-net (MIT): https://github.com/saul/demofile-net

/// <summary>
///     A list of ints representing a path through nested fields on server classes.
/// </summary>
internal struct FieldPath : IReadOnlyList<int>
{
    /// <summary>The default starting path used by the field-path decoder loop (single element <c>-1</c>).</summary>
    public static readonly FieldPath Default = new()
    {
        -1
    };

    private int _path0;
    private int _path1;
    private int _path2;
    private int _path3;
    private int _path4;
    private int _path5;
    private int _path6;

    /// <summary>Appends an integer to the path; max depth is 7 elements.</summary>
    public void Add(int item)
    {
        switch (Count)
        {
            case 0: _path0 = item; break;
            case 1: _path1 = item; break;
            case 2: _path2 = item; break;
            case 3: _path3 = item; break;
            case 4: _path4 = item; break;
            case 5: _path5 = item; break;
            case 6: _path6 = item; break;
            default: throw new InvalidOperationException("FieldPath is full");
        }

        Count += 1;
    }

    /// <summary>Removes the last <paramref name="count" /> elements from the path; clamps at zero.</summary>
    public void Pop(int count)
    {
        // Clamp rather than throw: misaligned streams can produce over-pops; silently cap at 0
        // so subsequent field-path ops skip their payload read rather than crashing.
        Count = Math.Max(0, Count - count);
    }

    /// <inheritdoc />
    public int this[int index]
    {
        readonly get => index >= 0 && index < Count
            ? index switch
            {
                0 => _path0,
                1 => _path1,
                2 => _path2,
                3 => _path3,
                4 => _path4,
                5 => _path5,
                6 => _path6,
                _ => throw new UnreachableException()
            }
            : throw new ArgumentOutOfRangeException(nameof(index), $"Cannot get item at index {index}, must be < {Count}");
        set
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Cannot set item at index {index}, must be < {Count}");
            }

            switch (index)
            {
                case 0: _path0 = value; break;
                case 1: _path1 = value; break;
                case 2: _path2 = value; break;
                case 3: _path3 = value; break;
                case 4: _path4 = value; break;
                case 5: _path5 = value; break;
                case 6: _path6 = value; break;
                default: throw new UnreachableException();
            }

            ;
        }
    }

    /// <inheritdoc />
    public int Count { get; private set; }

    /// <inheritdoc />
    public override string ToString() => Count == 0
        ? "(empty)"
        : "/" + string.Join('/', this);

    /// <inheritdoc />
    public readonly IEnumerator<int> GetEnumerator() => new Enumerator(in this);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(in this);

    /// <summary>Exposes the path as a zero-copy <see cref="ReadOnlySpan{T}" /> for fast iteration.</summary>
    public ReadOnlySpan<int> AsSpan() => MemoryMarshal.CreateReadOnlySpan(ref _path0, Count);

    /// <summary>Struct enumerator over a <see cref="FieldPath" /> — avoids the boxing of <see cref="IEnumerator{T}" />.</summary>
    public struct Enumerator : IEnumerator<int>
    {
        private readonly FieldPath _fieldPath;
        private int _index;

        internal Enumerator(in FieldPath fieldPath)
        {
            _index = -1;
            _fieldPath = fieldPath;
        }

        /// <inheritdoc />
        public int Current => _fieldPath[_index];

        object IEnumerator.Current => _fieldPath[_index];

        /// <inheritdoc />
        public void Dispose()
        {
            _index = _fieldPath.Count;
        }

        /// <inheritdoc />
        public bool MoveNext()
        {
            _index++;
            return _index < _fieldPath.Count;
        }

        /// <inheritdoc />
        public void Reset() => _index = -1;
    }
}
