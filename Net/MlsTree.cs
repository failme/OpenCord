using System.Runtime.InteropServices;

namespace ClaudeScord;

// ─────────────────────────────────────────────────────────────────────────────
// MLS ratchet tree (RFC 9420 §5, matching mlspp's treekem implementation).
// Array representation: leaves at even indices, parents at odd indices.
// ─────────────────────────────────────────────────────────────────────────────

static class TreeMath
{
    public static int Level(uint n)
    {
        int level = 0;
        while ((n & 1) == 1) { level++; n >>= 1; }
        return level;
    }

    public static uint Left(uint n) => n - (1u << (Level(n) - 1));
    public static uint Right(uint n) => n + (1u << (Level(n) - 1));

    public static uint Parent(uint n)
    {
        // RFC 9420 §5.1: k = level(x), b = bit (k+1) of x, then
        // parent(x) = (x | 2^k) XOR (b << (k+1)). Setting the lowest zero bit
        // alone is wrong for right children (parent(2) would be 3, not 1), which
        // made every right-subtree walk climb past the root forever.
        uint k = (uint)Level(n);
        uint b = (n >> (int)(k + 1)) & 1;
        return (n | (1u << (int)k)) ^ (b << (int)(k + 1));
    }

    public static uint Root(uint leafCount)
    {
        if (leafCount == 0) return 0;
        uint d = 0;
        while ((1u << (int)d) < leafCount) d++;
        return (1u << (int)d) - 1;
    }

    public static uint NodeCount(uint leafCount) => 2 * leafCount - 1;

    // Direct path: leaf up to root inclusive.
    public static List<uint> DirPath(uint leaf, uint size)
    {
        var path = new List<uint>();
        uint n = leaf;
        uint root = Root(size);
        while (true)
        {
            path.Add(n);
            if (n == root) break;
            n = Parent(n);
        }
        return path;
    }

    // Copath: siblings of all dirpath nodes except the root.
    public static List<uint> Copath(uint leaf, uint size)
    {
        var path = DirPath(leaf, size);
        var copath = new List<uint>();
        foreach (var n in path)
        {
            if (n == Root(size)) break;
            var p = Parent(n);
            copath.Add(Left(p) == n ? Right(p) : Left(p));
        }
        return copath;
    }

    // The node in the subtree of `node` that is on the path to `other`.
    public static uint Sibling(uint node, uint other)
    {
        if (node == other) return node;
        if (Level(node) == 0) return node == other ? node : throw new InvalidDataException("sibling of leaf");
        var l = Left(node);
        var r = Right(node);
        return IsBelow(l, other) ? r : l;
    }

    public static bool IsBelow(uint ancestor, uint node)
    {
        // Is `node` inside the subtree rooted at `ancestor`?
        uint level = (uint)Level(ancestor);
        uint span = 1u << (int)level;
        uint start = ancestor - (span - 1);
        return node >= start && node < start + 2 * span;
    }

    // Highest common ancestor node of two leaves.
    public static uint LeafAncestor(uint a, uint b)
    {
        if (a == b) return a;
        uint root = Root(Math.Max(a, b) + 1);
        var dir = DirPath(a, root + 1);
        uint n = b;
        while (true)
        {
            if (dir.Contains(n)) return n;
            if (n == root) return root;
            n = Parent(n);
        }
    }
}

sealed class MlsOptNode
{
    public bool IsLeaf;
    public byte[] EncKey = Array.Empty<byte>();
    public byte[] SigKey = Array.Empty<byte>();
    public byte[] Credential = Array.Empty<byte>();
    public byte[] Capabilities = Array.Empty<byte>();
    public int Source;
    public byte[] SourcePayload = Array.Empty<byte>();   // Lifetime or ParentHash
    public byte[] Extensions = Array.Empty<byte>();
    public byte[] Signature = Array.Empty<byte>();
    public byte[] ParentHash = Array.Empty<byte>();      // parents only
    public List<uint> Unmerged = new();

    public bool Blank => EncKey.Length == 0 && ParentHash.Length == 0;

    public byte[] LeafBytes => MlsLeafNode.Encode(EncKey, SigKey, Credential, Capabilities, Source, SourcePayload, Extensions, Signature);
    public byte[] ParentBytes => MlsParentNode.Encode(EncKey, ParentHash, Unmerged);
}

sealed class TreeKemPub
{
    public readonly Dictionary<uint, MlsOptNode> Nodes = new();
    public uint Size;                       // leaf count

    public MlsOptNode NodeAt(uint i) => Nodes[i];

    public bool IsBlank(uint i) => Nodes[i].Blank;

    public static TreeKemPub New(uint leafCount)
    {
        var t = new TreeKemPub { Size = leafCount };
        for (uint i = 0; i < TreeMath.NodeCount(leafCount); i++) t.Nodes[i] = new MlsOptNode();
        return t;
    }

    // ── resolution ─────────────────────────────────────────────────────────────
    public List<uint> Resolve(uint index)
    {
        var node = Nodes[index];
        if (!node.Blank)
        {
            if (TreeMath.Level(index) == 0) return new List<uint> { index };
            var outList = new List<uint> { index };
            foreach (var u in node.Unmerged) outList.Add(u);
            return outList;
        }
        if (TreeMath.Level(index) == 0) return new List<uint>();
        var res = Resolve(TreeMath.Left(index));
        res.AddRange(Resolve(TreeMath.Right(index)));
        return res;
    }

    // ── direct path / copath ───────────────────────────────────────────────────
    // Filtered direct path: (parent of each non-empty copath resolution, resolution).
    public List<(uint node, List<uint> res)> FilteredDirectPath(uint index)
    {
        var fdp = new List<(uint, List<uint>)>();
        foreach (var n in TreeMath.Copath(index, Size))
        {
            var p = TreeMath.Parent(n);
            var res = Resolve(n);
            if (res.Count == 0) continue;
            fdp.Add((p, res));
        }
        return fdp;
    }

    // ── tree hash ──────────────────────────────────────────────────────────────
    readonly Dictionary<uint, byte[]> _hashes = new();

    public void SetHashAll()
    {
        _hashes.Clear();
        for (uint i = 0; i < TreeMath.NodeCount(Size); i++) GetHash(i);
    }

    public void ClearHashPath(uint leaf)
    {
        foreach (var n in TreeMath.DirPath(leaf, Size)) _hashes.Remove(n);
    }

    public byte[] GetHash(uint index)
    {
        if (_hashes.TryGetValue(index, out var h)) return h;
        byte[] hashInput;
        if (TreeMath.Level(index) == 0)
        {
            // TreeHashInput{ node_type = 1, LeafNodeHashInput{ leaf_index u32, optional leaf } }.
            // leaf_index is the LEAF POSITION (node index / 2) — mlspp converts with
            // LeafIndex(NodeIndex x) = x.val / 2 before hashing. Writing the raw node
            // index (0, 2, 4, …) matched for a solo tree (leaf 0) but broke the tree
            // hash the moment a second member joined: the commit's GroupContext
            // tree_hash disagreed with the gateway's, the commit signature failed
            // verification, and the E2EE transition never started.
            var node = Nodes[index];
            var w = new TlsWriter();
            w.U8(1).U32(index / 2);
            // optional<LeafNode>: presence byte then the struct INLINE (mlspp
            // LeafNodeHashInput). Varint-wrapping the leaf here hashed a length
            // prefix the server never writes — the tree hash (and thus every
            // GroupContext / commit signature / welcome GroupInfo) disagreed with
            // mlspp's, so welcomes parsed but the tree hash never matched and the
            // E2EE transition never started.
            if (node.Blank) w.U8(0);
            else w.U8(1).Raw(node.LeafBytes);
            hashInput = w.Buf.ToArray();
        }
        else
        {
            var node = Nodes[index];
            var l = TreeMath.Left(index);
            var r = TreeMath.Right(index);
            var w = new TlsWriter();
            w.U8(2);
            // optional<ParentNode>: presence byte then the struct INLINE.
            if (node.Blank) w.U8(0);
            else w.U8(1).Raw(node.ParentBytes);
            w.Bytes(GetHash(l)).Bytes(GetHash(r));
            hashInput = w.Buf.ToArray();
        }
        h = MlsCrypto.Sha256(hashInput);
        _hashes[index] = h;
        return h;
    }

    public byte[] RootHash()
    {
        SetHashAll();
        return GetHash(TreeMath.Root(Size));
    }

    public byte[] OriginalTreeHash(uint index) => GetHash(index);

    // ── leaf management ────────────────────────────────────────────────────────
    // Leaf N (position 0, 1, 2, ...) lives at NODE index 2*N; parents are odd.
    // Returning the first free leaf as a node index keeps every TreeMath/TreeKemPub
    // call in node-index space; leaf indices only appear on the wire.
    public uint AllocateLeaf()
    {
        uint leaf = 0;
        while (leaf < Size && !Nodes[2 * leaf].Blank) leaf++;
        if (leaf >= Size)
        {
            Size = Size == 0 ? 1 : Size * 2;
            for (uint i = TreeMath.NodeCount(Size / 2); i < TreeMath.NodeCount(Size); i++)
                if (!Nodes.ContainsKey(i)) Nodes[i] = new MlsOptNode();
        }
        return 2 * leaf;
    }

    public uint AddLeaf(MlsOptNode leaf)
    {
        var index = AllocateLeaf();
        Nodes[index] = leaf;
        foreach (var n in TreeMath.DirPath(index, Size))
        {
            if (Nodes[n].Blank) continue;
            if (TreeMath.Level(n) == 0) continue;
            var list = Nodes[n].Unmerged;
            int pos = list.BinarySearch(index);
            if (pos < 0) list.Insert(~pos, index);
        }
        ClearHashPath(index);
        return index;
    }

    public void UpdateLeaf(uint index, MlsOptNode leaf)
    {
        Nodes[index] = leaf;
        ClearHashPath(index);
    }

    public void BlankPath(uint index)
    {
        Nodes[index] = new MlsOptNode();
        foreach (var n in TreeMath.DirPath(index, Size))
            if (n != index) Nodes[n] = new MlsOptNode();
        ClearHashPath(index);
    }

    public bool HasLeaf(uint index) => !Nodes[index].Blank;

    public MlsOptNode Leaf(uint index) => Nodes[index];

    // Returns the NODE index (2*leaf position) of the member whose encryption
    // key matches, or null.
    public uint? FindLeaf(byte[] encKey)
    {
        for (uint leaf = 0; leaf < Size; leaf++)
        {
            var n = Nodes[2 * leaf];
            if (!n.Blank && n.EncKey.SequenceEqual(encKey)) return 2 * leaf;
        }
        return null;
    }

    // ── parent hashes ──────────────────────────────────────────────────────────
    public byte[] ParentHash(byte[] pubKey, byte[] parentHash, uint copathChild)
    {
        var original = OriginalTreeHash(copathChild);
        var w = new TlsWriter();
        w.Bytes(pubKey).Bytes(parentHash).Bytes(original);
        return MlsCrypto.Sha256(CollectionsMarshal.AsSpan(w.Buf));
    }

    // Compute parent hashes for an update path; returns ph[0] = leaf's parent hash.
    public List<byte[]> ParentHashes(uint from, List<(uint node, List<uint> res)> fdp,
                                     List<byte[]> pathNodePubKeys)
    {
        if (fdp.Count == 0) return new List<byte[]>();
        var dp = new List<(uint node, List<uint> res)>(fdp);
        var last = dp[^1].node;
        dp.RemoveAt(dp.Count - 1);
        dp.Insert(0, (from, new List<uint>()));

        if (dp.Count != pathNodePubKeys.Count) throw new InvalidDataException("path size mismatch");

        var ph = new List<byte[]>(new byte[dp.Count][]);
        var lastHash = Array.Empty<byte>();
        for (int i = dp.Count - 1; i >= 0; i--)
        {
            var n = dp[i].node;
            var s = TreeMath.Sibling(n, last);
            lastHash = ParentHash(pathNodePubKeys[i], lastHash, s);
            ph[i] = lastHash;
            last = n;
        }
        return ph;
    }

    // ── merge an UpdatePath ────────────────────────────────────────────────────
    // The receiver recomputes the parent hashes exactly as the committer did
    // (RFC 9420 §7.5): without them the tree hash — and thus the confirmation
    // tag — would not match the committer's.
    public void Merge(uint from, byte[] leafNode,
                      List<(byte[] pubKey, List<(byte[] kem, byte[] ct)> cts)> pathNodes)
    {
        var leaf = ParseLeaf(leafNode);
        UpdateLeaf(from, leaf);
        var fdp = FilteredDirectPath(from);
        var pubs = pathNodes.Select(p => p.pubKey).ToList();
        var ph = pubs.Count > 0 ? ParentHashes(from, fdp, pubs) : new List<byte[]>();
        for (int i = 0; i < fdp.Count && i < pathNodes.Count; i++)
        {
            var n = fdp[i].node;
            var (pub, _) = pathNodes[i];
            if (!Nodes[n].Blank) throw new InvalidDataException("path node not blank");
            Nodes[n] = new MlsOptNode
            {
                EncKey = pub,
                ParentHash = ph.Count > i + 1 ? ph[i + 1] : Array.Empty<byte>(),
                Unmerged = new List<uint>(),
            };
        }
        ClearHashPath(from);
    }

    public static MlsOptNode ParseLeaf(byte[] data)
    {
        var (enc, sig, cred, caps, source, payload, exts, sigBytes, _) = MlsLeafNode.Decode(data);
        return new MlsOptNode
        {
            IsLeaf = true,
            EncKey = enc,
            SigKey = sig,
            Credential = cred,
            Capabilities = caps,
            Source = source,
            SourcePayload = payload,
            Extensions = exts,
            Signature = sigBytes,
        };
    }

    // ── serialization (ratchet_tree extension) ─────────────────────────────────
    public byte[] Serialize()
    {
        // In-order array from index 0 up to the last non-blank node (inclusive),
        // trailing blanks trimmed. The last node index is 2*Size - 2 (leaves sit
        // at even indices), not Size - 1 — starting there would silently drop
        // every member after the first from the welcome's tree.
        uint cut = TreeMath.NodeCount(Size) - 1;
        while (cut > 0 && Nodes[cut].Blank) cut--;
        var w = new TlsWriter();
        w.Vec(v =>
        {
            for (uint i = 0; i <= cut; i++)
            {
                var node = Nodes[i];
                if (node.Blank) v.U8(0);
                else if (TreeMath.Level(i) == 0)
                {
                    // optional<Node>: presence byte, NodeType tag, then the LeafNode
                    // INLINE (mlspp OptionalNode/Node). Varint-wrapping the node here
                    // produced welcomes even our own parser rejected ("varint
                    // underflow") whenever our client won the commit race and the
                    // gateway relayed our raw welcome to the joiner.
                    v.U8(1).U8(1).Raw(node.LeafBytes);      // present, leaf tag, inline leaf
                }
                else
                {
                    v.U8(1).U8(2).Raw(node.ParentBytes);    // present, parent tag, inline parent
                }
            }
        });
        return w.Buf.ToArray();
    }
}

// The tree serialization is an array of optional nodes; parse it with index tracking.
static class TreeSer
{
    public static Action<string>? Debug;   // probe diagnostics

    public static TreeKemPub Parse(byte[] data)
    {
        var r = new TlsReader(data);
        int count = r.VecLength();
        int end = r.Position + count;
        if (end > data.Length) throw new InvalidDataException("tree vector overrun");
        var list = new List<MlsOptNode?>();
        int idx = 0;
        while (r.Position < end)
        {
            int at = r.Position;
            int present = r.U8();
            if (present == 0) { list.Add(null); Debug?.Invoke($"tree[{idx}] blank @{at} rem={r.Remaining}"); idx++; continue; }
            if (present != 1) throw new InvalidDataException($"bad optional node @{at}");
            int type = r.U8();
            if (type == 1)
            {
                var (enc, sig, cred, caps, source, payload, exts, sigBytes, consumed) =
                    MlsLeafNode.Decode(data.AsSpan(r.Position));
                Debug?.Invoke($"tree[{idx}] leaf @{at} len={consumed} rem={r.Remaining} enc={enc.Length}B sig={sig.Length}B src={source}");
                r.Skip(consumed);
                list.Add(new MlsOptNode
                {
                    IsLeaf = true,
                    EncKey = enc,
                    SigKey = sig,
                    Credential = cred,
                    Capabilities = caps,
                    Source = source,
                    SourcePayload = payload,
                    Extensions = exts,
                    Signature = sigBytes,
                });
            }
            else if (type == 2)
            {
                int pstart = r.Position;
                var pk = r.Bytes();
                var ph = r.Bytes();
                var unmerged = new List<uint>();
                r.Vec(v => unmerged.Add(v.U32()));
                Debug?.Invoke($"tree[{idx}] parent @{at} len={r.Position - pstart} rem={r.Remaining} pk={pk.Length}B ph={ph.Length}B unmerged={unmerged.Count}");
                list.Add(new MlsOptNode { EncKey = pk, ParentHash = ph, Unmerged = unmerged });
            }
            else throw new InvalidDataException($"bad node type {type} @{at}");
            idx++;
        }
        if (list.Count % 2 == 0) throw new InvalidDataException($"even node count {list.Count}");
        if (list[^1] == null) throw new InvalidDataException("non-minimal tree");
        uint size = 1;
        while (TreeMath.NodeCount(size) < (uint)list.Count) size *= 2;
        var t = TreeKemPub.New(size);
        for (int i = 0; i < list.Count; i++)
            t.Nodes[(uint)i] = list[i] ?? new MlsOptNode();
        return t;
    }
}
