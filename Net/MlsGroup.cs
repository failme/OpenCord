namespace OpenCord;

// ─────────────────────────────────────────────────────────────────────────────
// TreeKEM private key state (path secrets + derived node keys), matching mlspp.
// ─────────────────────────────────────────────────────────────────────────────

sealed class TreeKemPriv
{
    public uint Index;
    public readonly Dictionary<uint, byte[]> PathSecrets = new();
    public readonly Dictionary<uint, (byte[] d, byte[] pub)> PrivCache = new();
    public byte[] UpdateSecret = new byte[32];

    public (byte[] d, byte[] pub)? PrivateKey(uint n)
    {
        if (PrivCache.TryGetValue(n, out var c)) return c;
        if (!PathSecrets.TryGetValue(n, out var ps)) return null;
        var nodeSecret = MlsCrypto.DeriveSecret(ps, "node");
        var (d, x, y) = MlsCrypto.DeriveP256(nodeSecret);
        var pub = MlsCrypto.PubPoint(x, y);
        PrivCache[n] = (d, pub);
        return (d, pub);
    }

    public bool HavePrivateKey(uint n) => PrivCache.ContainsKey(n) || PathSecrets.ContainsKey(n);

    // Solo group: the leaf key is generated directly (not derived).
    public static TreeKemPriv Solo(uint index, byte[] leafD, byte[] leafPub)
    {
        var p = new TreeKemPriv { Index = index };
        p.PrivCache[index] = (leafD, leafPub);
        return p;
    }

    // Committer update: leaf secret is the base of the path-secret chain.
    public static TreeKemPriv Create(TreeKemPub pub, uint from, byte[] leafSecret)
    {
        var p = new TreeKemPriv { Index = from };
        p.PathSecrets[from] = leafSecret;
        var secret = leafSecret;
        foreach (var (n, _) in pub.FilteredDirectPath(from))
        {
            secret = MlsCrypto.DeriveSecret(secret, "path");
            p.PathSecrets[n] = secret;
        }
        p.UpdateSecret = MlsCrypto.DeriveSecret(secret, "path");
        return p;
    }

    // Welcome joiner: leaf key from key package, path secret implanted at the
    // intersect node and derived upward.
    public static TreeKemPriv Joiner(TreeKemPub pub, uint index, byte[] leafD, byte[] leafPub,
                                     uint intersect, byte[]? pathSecret)
    {
        var p = new TreeKemPriv { Index = index };
        p.PrivCache[index] = (leafD, leafPub);
        if (pathSecret != null)
        {
            p.PathSecrets[intersect] = pathSecret;
            var secret = pathSecret;
            foreach (var n in TreeMath.DirPath(intersect, pub.Size))
            {
                if (n == intersect) continue;
                if (pub.IsBlank(n)) continue;
                secret = MlsCrypto.DeriveSecret(secret, "path");
                p.PathSecrets[n] = secret;
            }
            p.UpdateSecret = MlsCrypto.DeriveSecret(secret, "path");
        }
        return p;
    }

    public (uint node, byte[] secret, bool ok) SharedPathSecret(uint to)
    {
        var n = TreeMath.LeafAncestor(Index, to);
        if (!PathSecrets.TryGetValue(n, out var s)) return (n, Array.Empty<byte>(), false);
        return (n, s, true);
    }

    // Decrypt one encrypted path secret and implant it.
    public bool Decap(TreeKemPub pub, uint from, byte[] context, uint decryptNode,
                      byte[] kem, byte[] ct)
    {
        var overlap = TreeMath.LeafAncestor(from, Index);
        var priv = PrivateKey(decryptNode);
        if (priv == null) return false;
        var info = MlsCrypto.MlsEncryptInfo("UpdatePathNode", context);
        var pathSecret = MlsCrypto.HpkeOpen(kem, priv.Value.d, info, Array.Empty<byte>(), ct, priv.Value.pub);
        if (pathSecret == null) return false;
        Implant(pub, overlap, pathSecret);
        return true;
    }

    void Implant(TreeKemPub pub, uint start, byte[] pathSecret)
    {
        PathSecrets[start] = pathSecret;
        PrivCache.Remove(start);
        var secret = pathSecret;
        foreach (var (n, _) in pub.FilteredDirectPath(start))
        {
            secret = MlsCrypto.DeriveSecret(secret, "path");
            PathSecrets[n] = secret;
            PrivCache.Remove(n);
        }
        UpdateSecret = MlsCrypto.DeriveSecret(secret, "path");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// A single MLS group: public tree, private keys, key schedule, transcripts.
// Supports create / commit / process-commit / welcome.
// ─────────────────────────────────────────────────────────────────────────────

sealed class MlsGroup
{
    public byte[] GroupId = Array.Empty<byte>();
    public ulong Epoch;
    public TreeKemPub Tree = TreeKemPub.New(1);
    public TreeKemPriv TreePriv = null!;
    public MlsCrypto.KeySchedule Keys = null!;
    public uint Index;
    public byte[] Extensions = Array.Empty<byte>();
    public byte[] ConfirmedTranscript = Array.Empty<byte>();
    byte[] _interimTranscript = Array.Empty<byte>();

    public byte[] SelfEncD = Array.Empty<byte>();
    public byte[] SelfEncPub = Array.Empty<byte>();
    public byte[] SelfSigD = Array.Empty<byte>();
    public byte[] SelfSigPub = Array.Empty<byte>();
    public byte[] Identity = Array.Empty<byte>();
    public byte[] ExtensionsData = Array.Empty<byte>();   // external senders ext data

    public readonly List<(byte[] refHash, int type, byte[] payload)> Pending = new();

    // Cached outbound state: the commit+welcome we built is NOT applied until the
    // gateway broadcasts the winning commit back (op 29) — own commits are then
    // adopted via ProcessOwnCommit (mlspp's outboundCachedGroupState).
    byte[]? _outCommit;
    byte[] _outConfirmedHash = null!, _outConfTag = null!;
    TreeKemPub _outTree = null!;
    TreeKemPriv _outTreePriv = null!;
    MlsCrypto.KeySchedule _outKeys = null!;
    ulong _outEpoch;

    public byte[] GroupContextBytes() =>
        MlsGroupContext.Encode(GroupId, Epoch, Tree.RootHash(), ConfirmedTranscript, Extensions);

    // ── create: a 1-member group ───────────────────────────────────────────────
    public static MlsGroup Create(byte[] groupId, byte[] selfEncD, byte[] selfEncPub,
                                  byte[] selfSigD, byte[] selfSigPub, byte[] identity,
                                  byte[] extensions, byte[] extensionsData)
    {
        var g = new MlsGroup
        {
            GroupId = groupId,
            Tree = TreeKemPub.New(1),
            SelfEncD = selfEncD,
            SelfEncPub = selfEncPub,
            SelfSigD = selfSigD,
            SelfSigPub = selfSigPub,
            Identity = identity,
            Extensions = extensions,
            ExtensionsData = extensionsData,
        };

        var leaf = g.SelfLeaf(MlsLeafNode.SourceKeyPackage, MlsLifetime.EncodeMax(), null);
        g.Tree.UpdateLeaf(0, TreeKemPub.ParseLeaf(leaf));
        g.Index = 0;
        g.TreePriv = TreeKemPriv.Solo(0, selfEncD, selfEncPub);

        g.Keys = MlsCrypto.KeySchedule.Create(MlsRandom.Bytes(32), g.GroupContextBytes());
        g.ConfirmedTranscript = Array.Empty<byte>();
        g._interimTranscript = MlsCrypto.Sha256(
            Concat(g.ConfirmedTranscript, Tls.Bytes(g.Keys.ConfirmationTag)));
        return g;
    }

    // The group's leaf node (source = key_package) and its key-package sibling.
    byte[] SelfLeaf(int source, byte[] sourcePayload, (byte[] gid, uint index)? binding)
    {
        var caps = MlsCapabilities.EncodeDefault();
        var credential = MlsCredential.Encode(Identity);
        var noExts = MlsExtensions.EncodeList();
        var tbs = LeafNodeTbs(SelfEncPub, SelfSigPub, credential, caps, source, sourcePayload, noExts, binding);
        var sig = MlsCrypto.SignWithLabel(SelfSigD, "LeafNodeTBS", tbs);
        return MlsLeafNode.Encode(SelfEncPub, SelfSigPub, credential, caps, source, sourcePayload, noExts, sig);
    }

    public byte[] BuildKeyPackage(byte[] initPub)
    {
        var leaf = SelfLeaf(MlsLeafNode.SourceKeyPackage, MlsLifetime.EncodeMax(), null);
        var noExts = MlsExtensions.EncodeList();
        var tbs = new TlsWriter().U16(1).U16(2).Bytes(initPub).Raw(leaf).Raw(noExts).Buf.ToArray();
        var sig = MlsCrypto.SignWithLabel(SelfSigD, "KeyPackageTBS", tbs);
        return MlsKeyPackage.Encode(initPub, leaf, noExts, sig);
    }

    // RFC 9420 §12.2 key package validation, mirroring the voice gateway: the leaf
    // signature covers LeafNodeTBS (inline structs, Lifetime inline) and the key
    // package signature covers KeyPackageTBS (LeafNode + ExtensionList inline).
    // The gateway verifies both before accepting an Add proposal; verifying here
    // too keeps a malformed package out of the group and gives the two-party
    // self-test real teeth (it drives DaveMls.BuildKeyPackage end to end).
    public static bool VerifyKeyPackage(byte[] keyPackage)
    {
        try
        {
            var (initKey, leaf, exts, sig, _) = MlsKeyPackage.Decode(keyPackage);
            var (enc, sigKey, cred, caps, source, payload, leafExts, leafSig, _) = MlsLeafNode.Decode(leaf);
            if (source != MlsLeafNode.SourceKeyPackage) return false;
            var leafTbs = LeafNodeTbs(enc, sigKey, cred, caps, source, payload, leafExts, null);
            var (x, y) = MlsCrypto.SplitPoint(sigKey);
            if (!MlsCrypto.VerifyWithLabel(x, y, "LeafNodeTBS", leafTbs, leafSig)) return false;
            var kpTbs = new TlsWriter().U16(1).U16(2).Bytes(initKey).Raw(leaf).Raw(exts).Buf.ToArray();
            return MlsCrypto.VerifyWithLabel(x, y, "KeyPackageTBS", kpTbs, sig);
        }
        catch { return false; }
    }

    // ── external-sender proposals (op 27) ──────────────────────────────────────
    public bool ProcessProposal(byte[] mlsMessage, byte[] externalSigPub)
    {
        try
        {
            var (wire, authContent, _) = MlsMessage.Decode(mlsMessage);
            var (gid, epoch, senderType, senderIdx, contentType, content, sig, _, _, _) =
                MlsAuthContent.Decode(authContent);
            if (wire != 1 || contentType != MlsAuthContent.ContentProposal) return false;
            if (senderType != MlsAuthContent.SenderExternal) return false;
            if (!gid.SequenceEqual(GroupId) || epoch != Epoch) return false;

            var (x, y) = MlsCrypto.SplitPoint(externalSigPub);
            var tbs = ContentTbs(senderType, senderIdx, contentType, content, null);
            if (!MlsCrypto.VerifyWithLabel(x, y, "FramedContentTBS", tbs, sig)) return false;

            var (propType, payload, _) = MlsProposal.Decode(content);
            if (propType != MlsProposal.Add && propType != MlsProposal.Remove) return false;
            if (propType == MlsProposal.Remove)
            {
                var r = new TlsReader(payload);
                if (r.Remaining != 4) return false;
            }
            if (propType == MlsProposal.Add && !VerifyKeyPackage(payload)) return false;

            // The proposal ref hashes the AuthenticatedContent, which starts with the
            // wire format (RFC 9420 §12.1); the serialized PublicMessage body omits it.
            Pending.Add((MlsCrypto.ProposalRef(Concat(Tls.U16(1), authContent)), propType, payload));
            return true;
        }
        catch { return false; }
    }

    // ── commit + welcome (op 28) ───────────────────────────────────────────────
    // Returns the commit MLSMessage and welcome WITHOUT mutating this group. The
    // next state is cached and adopted when the gateway broadcasts the winning
    // commit back via op 29 (mlspp's outboundCachedGroupState).
    public (byte[] commitMessage, byte[] welcome) BuildCommitAndWelcome()
    {
        if (Pending.Count == 0) throw new InvalidDataException("nothing to commit");

        var newTree = CloneTree(Tree);
        var joiners = new List<byte[]>();
        var joinerLocs = new List<uint>();
        bool hasRemove = false;
        foreach (var (_, type, payload) in Pending)
        {
            if (type == MlsProposal.Add)
            {
                var kp = payload;
                joiners.Add(kp);
                var (_, leafNode, _, _, _) = MlsKeyPackage.Decode(kp);
                joinerLocs.Add(newTree.AddLeaf(TreeKemPub.ParseLeaf(leafNode)));
            }
            else
            {
                var r = new TlsReader(payload);
                var removed = r.U32();                  // leaf index on the wire
                if (!newTree.HasLeaf(2 * removed)) throw new InvalidDataException("remove non-member");
                newTree.BlankPath(2 * removed);
                hasRemove = true;
            }
        }

        // A commit that removes members must carry an UpdatePath (mlspp
        // path_required). DAVE group creation is add-only, so the common case has
        // no path and a zero commit secret.
        byte[]? pathBytes = null;
        byte[] commitSecret = MlsCrypto.ZeroSecret;
        TreeKemPriv? newPriv = null;
        if (hasRemove)
        {
            var leafSecret = MlsRandom.Bytes(32);
            newPriv = TreeKemPriv.Create(newTree, Index, leafSecret);
            var fdp = newTree.FilteredDirectPath(Index);
            var nodePubKeys = new List<byte[]>();
            foreach (var (node, _) in fdp)
            {
                var ns = MlsCrypto.DeriveSecret(newPriv.PathSecrets[node], "node");
                var (_, xx, yx) = MlsCrypto.DeriveP256(ns);
                nodePubKeys.Add(MlsCrypto.PubPoint(xx, yx));
            }
            var ph = newTree.ParentHashes(Index, fdp, nodePubKeys);
            var newLeaf = CommitLeaf(ph.Count > 0 ? ph[0] : Array.Empty<byte>());
            newTree.UpdateLeaf(Index, TreeKemPub.ParseLeaf(newLeaf));

            // Encrypt each path secret to the resolution of its copath node. The
            // context is the new GroupContext with the *current* confirmed
            // transcript hash (mlspp prepare_commit does exactly this).
            var encCtx = MlsGroupContext.Encode(GroupId, Epoch + 1, newTree.RootHash(),
                                                ConfirmedTranscript, Extensions);
            var info = MlsCrypto.MlsEncryptInfo("UpdatePathNode", encCtx);
            var pathNodes = new List<byte[]>();
            for (int i = 0; i < fdp.Count; i++)
            {
                var (node, res) = fdp[i];
                var ps = newPriv.PathSecrets[node];
                var cts = new List<byte[]>();
                foreach (var rn in res)
                {
                    var (enc, ct) = MlsCrypto.HpkeSeal(newTree.Leaf(rn).EncKey, info,
                                                       Array.Empty<byte>(), ps);
                    cts.Add(MlsHpkeCiphertext.Encode(enc, ct));
                }
                var parentHash = ph.Count > i + 1 ? ph[i + 1] : Array.Empty<byte>();
                newTree.Nodes[node] = new MlsOptNode { EncKey = nodePubKeys[i], ParentHash = parentHash, Unmerged = new List<uint>() };
                pathNodes.Add(MlsUpdatePath.EncodeNode(nodePubKeys[i], cts));
            }
            pathBytes = MlsUpdatePath.Encode(newLeaf, pathNodes);
            commitSecret = newPriv.UpdateSecret;
        }

        var proposals = new List<(int tag, byte[] content)>();
        foreach (var (refHash, _, _) in Pending)
            proposals.Add((MlsAuthContent.ProposalOrRefRef, refHash));

        var commitBody = MlsCommit.Encode(proposals, pathBytes);
        var (_, signature) = SignContent(MlsAuthContent.SenderMember, Index,
                                         MlsAuthContent.ContentCommit, commitBody);
        var confirmedInput = ConfirmedInput(MlsAuthContent.SenderMember, Index,
                                            MlsAuthContent.ContentCommit, commitBody, signature);
        var newConfirmedHash = MlsCrypto.Sha256(Concat(_interimTranscript, confirmedInput));

        // The key schedule derives with the NEW confirmed transcript hash in the
        // GroupContext (mlspp successor).
        var ctx = MlsGroupContext.Encode(GroupId, Epoch + 1, newTree.RootHash(),
                                         newConfirmedHash, Extensions);
        var ks = Keys.Next(commitSecret, ctx, newConfirmedHash);
        var confirmationTag = ks.ConfirmationTag;

        // Member-sent PublicMessages MUST carry a membership tag: the TBS (old
        // epoch's context) followed by the auth (signature + NEW confirmation
        // tag), HMAC'd with the OLD epoch's membership key — mlspp protect() uses
        // the pre-commit key schedule and group context, with the confirmation
        // tag set just before. mlspp's parser reads this tag after the auth, so
        // a tagless commit is rejected by Discord's gateway.
        var contentBytes = ContentBytes(MlsAuthContent.SenderMember, Index,
                                        MlsAuthContent.ContentCommit, commitBody);
        var tbm = Concat(Tls.U16(1), Tls.U16(1), contentBytes, GroupContextBytes(),
                         Tls.Bytes(signature), Tls.Bytes(confirmationTag));
        var membershipTag = MlsCrypto.Hmac(Keys.MembershipKey, tbm);

        var authContent = MlsAuthContent.EncodePublicMessage(
            GroupId, Epoch, MlsAuthContent.SenderMember, Index,
            MlsAuthContent.ContentCommit, commitBody, signature, confirmationTag, membershipTag);
        var commitMessage = MlsMessage.Encode(authContent);

        byte[] welcome = Array.Empty<byte>();
        if (joiners.Count > 0)
        {
            // mlspp's GroupInfo extensions carry ONLY the ratchet tree (the external
            // senders extension lives in the GroupContext, not here). Adding it twice
            // made our welcome ~80B larger than the gateway's and broke joiners that
            // reject the duplicate extension.
            var treeExt = MlsExtensions.EncodeList(
                (MlsExtensions.RatchetTree, newTree.Serialize()));
            var groupInfo = BuildGroupInfo(ctx, treeExt, confirmationTag);
            welcome = BuildWelcome(ks, groupInfo, joiners, joinerLocs, pathBytes != null);
        }

        _outCommit = commitMessage;
        _outTree = newTree;
        _outTreePriv = newPriv ?? TreePriv;
        _outKeys = ks;
        _outEpoch = Epoch + 1;
        _outConfirmedHash = newConfirmedHash;
        _outConfTag = confirmationTag;
        return (commitMessage, welcome);
    }

    // Adopt our own broadcast commit (op 29): verify it is the message we sent
    // and switch to the precomputed next state.
    public bool ProcessOwnCommit(byte[] commitMessage)
    {
        if (_outCommit == null || !_outCommit.SequenceEqual(commitMessage)) return false;
        Tree = _outTree;
        TreePriv = _outTreePriv;
        Keys = _outKeys;
        Epoch = _outEpoch;
        ConfirmedTranscript = _outConfirmedHash;
        _interimTranscript = MlsCrypto.Sha256(Concat(_outConfirmedHash, Tls.Bytes(_outConfTag)));
        Pending.Clear();
        _outCommit = null;
        return true;
    }

    // op 27 revoke: drop cached proposals whose refs are listed.
    public void RevokeProposals(List<byte[]> refs)
    {
        foreach (var r in refs)
            Pending.RemoveAll(p => p.refHash.SequenceEqual(r));
    }

    // ── process a broadcast commit (op 29) ─────────────────────────────────────
    public bool ProcessCommit(byte[] mlsMessage, bool isOwn)
    {
        try
        {
            var (wire, authContent, _) = MlsMessage.Decode(mlsMessage);
            var (gid, epoch, senderType, senderIdx, contentType, content, sig, conf, memTag, _) =
                MlsAuthContent.Decode(authContent);
            if (wire != 1 || contentType != MlsAuthContent.ContentCommit) return false;
            if (senderType != MlsAuthContent.SenderMember) return false;
            if (!gid.SequenceEqual(GroupId) || epoch != Epoch || conf == null) return false;
            if (senderIdx >= Tree.Size || !Tree.HasLeaf(2 * senderIdx)) return false;

            // Member-sent commits must carry a membership tag verified against
            // the CURRENT membership key + group context (mlspp unprotect).
            if (memTag == null) return false;
            var myContentBytes = ContentBytes(senderType, senderIdx, contentType, content);
            var myTbm = Concat(Tls.U16(1), Tls.U16(1), myContentBytes, GroupContextBytes(),
                               Tls.Bytes(sig), Tls.Bytes(conf));
            if (!MlsCrypto.Hmac(Keys.MembershipKey, myTbm).SequenceEqual(memTag)) return false;

            var committer = Tree.Leaf(2 * senderIdx);   // senderIdx is a leaf index
            var (cx, cy) = MlsCrypto.SplitPoint(committer.SigKey);
            var tbs = ContentTbs(senderType, senderIdx, contentType, content, GroupContextBytes());
            if (!MlsCrypto.VerifyWithLabel(cx, cy, "FramedContentTBS", tbs, sig)) return false;

            var (propRefs, path, _) = MlsCommit.Decode(content);

            var newTree = CloneTree(Tree);
            var joinerLocs = new List<uint>();
            foreach (var (tag, p) in propRefs)
            {
                if (tag != MlsAuthContent.ProposalOrRefRef) return false;
                var idx = Pending.FindIndex(pd => pd.refHash.SequenceEqual(p));
                if (idx < 0) return false;
                var (_, type, payload) = Pending[idx];
                if (type == MlsProposal.Add)
                {
                    var (_, leafNode, _, _, _) = MlsKeyPackage.Decode(payload);
                    joinerLocs.Add(newTree.AddLeaf(TreeKemPub.ParseLeaf(leafNode)));
                }
                else
                {
                    var r = new TlsReader(payload);
                    var removed = r.U32();              // leaf index on the wire
                    if (!newTree.HasLeaf(2 * removed)) return false;
                    newTree.BlankPath(2 * removed);
                }
            }
            Pending.Clear();

            var confirmedInput = ConfirmedInput(senderType, senderIdx, contentType, content, sig);
            var newConfirmedHash = MlsCrypto.Sha256(Concat(_interimTranscript, confirmedInput));

            var newTreePriv = TreePriv;
            var commitSecret = MlsCrypto.ZeroSecret;
            if (path != null)
            {
                var (pathLeaf, pathNodes, _) = MlsUpdatePath.Decode(path);
                var decodedNodes = pathNodes.Select(n => MlsUpdatePath.DecodeNode(n)).ToList();
                newTree.Merge(2 * senderIdx, pathLeaf, decodedNodes);
                var fdp = newTree.FilteredDirectPath(2 * senderIdx);

                // Path secrets were encrypted to the new GroupContext with the
                // *current* confirmed transcript hash (mlspp ratchet/decap).
                var ctxDecap = MlsGroupContext.Encode(GroupId, Epoch + 1, newTree.RootHash(),
                                                      ConfirmedTranscript, Extensions);
                bool found = false;
                for (int dpi = 0; dpi < fdp.Count && !found; dpi++)
                {
                    var (dpn, res) = fdp[dpi];
                    if (!TreeMath.IsBelow(dpn, Index)) continue;
                    // Resolution minus the newly added joiners (they decrypt via
                    // the welcome instead).
                    var resList = res.Where(n => !joinerLocs.Contains(n)).ToList();
                    for (int i = 0; i < resList.Count; i++)
                    {
                        if (!newTreePriv.HavePrivateKey(resList[i])) continue;
                        var nodeData = decodedNodes[dpi];
                        if (i >= nodeData.cts.Count) return false;
                        var (kem, ct) = nodeData.cts[i];
                        if (!newTreePriv.Decap(newTree, 2 * senderIdx, ctxDecap, resList[i], kem, ct))
                            return false;
                        commitSecret = newTreePriv.UpdateSecret;
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }

            // The key schedule uses the NEW confirmed transcript hash in its
            // GroupContext (mlspp successor).
            var ctx = MlsGroupContext.Encode(GroupId, Epoch + 1, newTree.RootHash(),
                                             newConfirmedHash, Extensions);
            var ks = Keys.Next(commitSecret, ctx, newConfirmedHash);
            if (!ks.ConfirmationTag.SequenceEqual(conf)) return false;

            Tree = newTree;
            TreePriv = newTreePriv;
            Epoch += 1;
            Keys = ks;
            ConfirmedTranscript = newConfirmedHash;
            _interimTranscript = MlsCrypto.Sha256(Concat(newConfirmedHash, Tls.Bytes(conf)));
            return true;
        }
        catch { return false; }
    }

    public static Action<string>? Debug;   // step-by-step welcome/commit diagnostics (probe)

    static void Db(string line) => Debug?.Invoke(line);

    // ── join via welcome (op 30) ───────────────────────────────────────────────
    public static MlsGroup? FromWelcome(byte[] welcomeBytes, byte[] myKeyPackage,
                                        byte[] joinInitD, byte[] joinInitPub,
                                        byte[] selfEncD, byte[] selfEncPub,
                                        byte[] selfSigD, byte[] selfSigPub,
                                        byte[] identity)
    {
        try
        {
            Db($"welcome {welcomeBytes.Length}B kp {myKeyPackage.Length}B");
            var (suite, secrets, encGroupInfo) = MlsWelcome.Decode(welcomeBytes);
            Db($"suite={suite} secrets={secrets.Count} encGroupInfo={encGroupInfo.Length}B");
            if (suite != 2) { Db("fail: suite != 2"); return null; }

            var kpRef = MlsCrypto.KeyPackageRef(myKeyPackage);
            var encGs = secrets.FirstOrDefault(s => s.kpRef.SequenceEqual(kpRef));
            if (encGs.kpRef == null)
            {
                Db($"fail: kpRef not in welcome (mine={Convert.ToHexString(kpRef)[..16]}... welcome={string.Join(",", secrets.Select(s => Convert.ToHexString(s.kpRef)[..16]))})");
                return null;
            }

            var (kem, ct) = MlsHpkeCiphertext.Decode(encGs.encGroupSecrets);
            var info = MlsCrypto.MlsEncryptInfo("Welcome", encGroupInfo);
            var gsPlain = MlsCrypto.HpkeOpen(kem, joinInitD, info, Array.Empty<byte>(), ct, joinInitPub);
            if (gsPlain == null) { Db("fail: HPKE open group secrets"); return null; }
            Db($"HPKE open ok, group secrets {gsPlain.Length}B");
            var (joinerSecret, pathSecret) = MlsGroupSecrets.Decode(gsPlain);
            Db($"joiner secret {joinerSecret.Length}B pathSecret={pathSecret?.Length ?? -1}");

            var (wKey, wNonce) = MlsCrypto.KeySchedule.WelcomeKeyNonce(joinerSecret);
            var groupInfoPlain = MlsCrypto.Aes128GcmOpen(wKey, wNonce, Array.Empty<byte>(), encGroupInfo);
            if (groupInfoPlain == null) { Db("fail: AES-GCM open group info"); return null; }
            Db($"group info {groupInfoPlain.Length}B");
            var (gc, extList, confTag, signer, giSig, _) = MlsGroupInfo.Decode(groupInfoPlain);
            Db($"group info decoded: confTag={confTag.Length}B signer={signer} exts={Convert.ToHexString(extList)[..Math.Min(32, extList.Length)]}");

            var (gid, epoch, treeHash, confirmedHash, extensions) = DecodeGroupContext(gc);
            Db($"gid={Convert.ToHexString(gid)} epoch={epoch} treeHash={Convert.ToHexString(treeHash)[..16]}... ext={extensions.Length}B");
            if (epoch == 0) { Db("fail: epoch 0"); return null; }

            var treeExts = MlsExtensions.DecodeList(extList);
            Db($"groupinfo exts: {string.Join(",", treeExts.Select(e => e.type.ToString()))}");
            var treeData = treeExts.FirstOrDefault(e => e.type == MlsExtensions.RatchetTree).data;
            if (treeData == null) { Db("fail: no ratchet tree in group info"); return null; }
            Db($"tree data {treeData.Length}B hex={Convert.ToHexString(treeData, 0, Math.Min(treeData.Length, 300))}");
            TreeSer.Debug = l => Db(l);
            TreeKemPub tree;
            try { tree = TreeSer.Parse(treeData); }
            finally { TreeSer.Debug = null; }
            Db($"tree parsed: size={tree.Size} rootHash={Convert.ToHexString(tree.RootHash())[..16]}...");
            if (!tree.RootHash().SequenceEqual(treeHash)) { Db("fail: tree hash mismatch"); return null; }

            if (signer >= tree.Size || !tree.HasLeaf(2 * signer)) { Db($"fail: signer {signer} not a member"); return null; }
            var signerLeaf = tree.Leaf(2 * signer);     // signer is a leaf index
            var (sx, sy) = MlsCrypto.SplitPoint(signerLeaf.SigKey);
            var giTbs = new TlsWriter().Raw(gc).Raw(extList).Bytes(confTag).U32(signer).Buf.ToArray();
            if (!MlsCrypto.VerifyWithLabel(sx, sy, "GroupInfoTBS", giTbs, giSig)) { Db("fail: group info signature"); return null; }
            Db("group info signature ok");

            var myIndex = tree.FindLeaf(selfEncPub);
            if (myIndex == null) { Db("fail: my leaf not in tree"); return null; }
            Db($"my leaf at {myIndex.Value}");

            var group = new MlsGroup
            {
                GroupId = gid,
                Epoch = epoch,
                Tree = tree,
                Index = myIndex.Value,
                Extensions = extensions,
                ConfirmedTranscript = confirmedHash,
                SelfEncD = selfEncD,
                SelfEncPub = selfEncPub,
                SelfSigD = selfSigD,
                SelfSigPub = selfSigPub,
                Identity = identity,
            };
            var ctx = MlsGroupContext.Encode(gid, epoch, treeHash, confirmedHash, extensions);
            group.Keys = MlsCrypto.KeySchedule.Joiner(joinerSecret, ctx, confirmedHash);
            if (!group.Keys.ConfirmationTag.SequenceEqual(confTag)) { Db("fail: confirmation tag"); return null; }
            Db("confirmation tag ok");
            group._interimTranscript = MlsCrypto.Sha256(Concat(confirmedHash, Tls.Bytes(confTag)));

            var ancestor = TreeMath.LeafAncestor(myIndex.Value, 2 * signer);
            group.TreePriv = TreeKemPriv.Joiner(tree, myIndex.Value, selfEncD, selfEncPub, ancestor, pathSecret);
            Db("welcome processed OK");
            return group;
        }
        catch (Exception e) { Db("welcome exception: " + e.Message); return null; }
    }

    // ── roster / sender keys ───────────────────────────────────────────────────
    // userId (snowflake) → leaf signature key + leaf index.
    public Dictionary<ulong, byte[]> Roster()
    {
        var roster = new Dictionary<ulong, byte[]>();
        for (uint leaf = 0; leaf < Tree.Size; leaf++)
        {
            var n = Tree.Nodes[2 * leaf];
            if (n.Blank) continue;
            try
            {
                var (type, identity) = MlsCredential.Decode(n.Credential);
                if (type != MlsCredential.Basic || identity.Length != 8) continue;
                var uid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(identity);
                roster[uid] = n.SigKey;
            }
            catch { }
        }
        return roster;
    }

    public byte[] SenderBaseSecret(ulong userId) =>
        Keys.Export("Discord Secure Frames v0", Le64(userId), 16);

    // ── helpers ────────────────────────────────────────────────────────────────
    (byte[] contentBytes, byte[] signature) SignContent(int senderType, uint senderIdx,
                                                        int contentType, byte[] content)
    {
        var contentBytes = ContentBytes(senderType, senderIdx, contentType, content);
        var ctx = senderType == MlsAuthContent.SenderMember ? GroupContextBytes() : null;
        var tbs = Concat(Tls.U16(1), Tls.U16(1), contentBytes, ctx ?? Array.Empty<byte>());
        var sig = MlsCrypto.SignWithLabel(SelfSigD, "FramedContentTBS", tbs);
        return (contentBytes, sig);
    }

    byte[] ContentBytes(int senderType, uint senderIdx, int contentType, byte[] content)
    {
        var w = new TlsWriter();
        w.Bytes(GroupId).U64(Epoch).U8(senderType).U32(senderIdx);
        w.Vec(v => { });
        w.U8(contentType).Raw(content);
        return w.Buf.ToArray();
    }

    byte[] ContentTbs(int senderType, uint senderIdx, int contentType, byte[] content, byte[]? groupContext)
        => Concat(Tls.U16(1), Tls.U16(1), ContentBytes(senderType, senderIdx, contentType, content),
                  groupContext ?? Array.Empty<byte>());

    // ConfirmedTranscriptTBS (RFC 9420 §8.1): wire_format || FramedContent || signature
    // — the confirmation tag is derived FROM the confirmed hash and is not included
    // (matches mlspp's confirmed_transcript_hash_input()).
    byte[] ConfirmedInput(int senderType, uint senderIdx, int contentType, byte[] content, byte[] signature)
    {
        var w = new TlsWriter();
        w.Bytes(GroupId).U64(Epoch).U8(senderType).U32(senderIdx);
        w.Vec(v => { });
        w.U8(contentType).Raw(content);
        return Concat(Tls.U16(1), w.Buf.ToArray(), Tls.Bytes(signature));
    }

    byte[] BuildGroupInfo(byte[] context, byte[] extensions, byte[] confirmationTag)
    {
        var tbs = new TlsWriter().Raw(context).Raw(extensions).Bytes(confirmationTag).U32(Index).Buf.ToArray();
        var sig = MlsCrypto.SignWithLabel(SelfSigD, "GroupInfoTBS", tbs);
        return MlsGroupInfo.Encode(context, extensions, confirmationTag, Index, sig);
    }

    byte[] BuildWelcome(MlsCrypto.KeySchedule ks, byte[] groupInfo,
                        List<byte[]> joiners, List<uint> joinerLocs, bool hasPath)
    {
        var (wKey, wNonce) = MlsCrypto.KeySchedule.WelcomeKeyNonce(ks.JoinerSecret);
        var encGroupInfo = MlsCrypto.Aes128GcmSeal(wKey, wNonce, Array.Empty<byte>(), groupInfo);
        var secrets = new List<(byte[] kpRef, byte[] encGs)>();
        for (int i = 0; i < joiners.Count; i++)
        {
            var kp = joiners[i];
            var (initKey, _, _, _, _) = MlsKeyPackage.Decode(kp);
            byte[]? pathSecret = null;
            // A joiner only receives a path secret when this commit carries an
            // UpdatePath (mlspp: shared_path_secret is only set in that branch).
            if (hasPath && joinerLocs.Count > i)
            {
                var (_, s, ok) = TreePriv.SharedPathSecret(joinerLocs[i]);
                if (ok) pathSecret = s;
            }
            var gs = MlsGroupSecrets.Encode(ks.JoinerSecret, pathSecret);
            var info = MlsCrypto.MlsEncryptInfo("Welcome", encGroupInfo);
            var (enc, ct) = MlsCrypto.HpkeSeal(initKey, info, Array.Empty<byte>(), gs);
            secrets.Add((MlsCrypto.KeyPackageRef(kp), MlsHpkeCiphertext.Encode(enc, ct)));
        }
        return MlsWelcome.Encode(secrets, encGroupInfo);
    }

    // A commit-source leaf: keeps the same identity keys, binds the parent hash.
    // A commit-source leaf: keeps the same identity keys, binds the parent hash and
    // the leaf-node binding (group id + leaf index) in the TBS.
    byte[] CommitLeaf(byte[] parentHash)
    {
        var caps = MlsCapabilities.EncodeDefault();
        var credential = MlsCredential.Encode(Identity);
        var noExts = MlsExtensions.EncodeList();
        var tbs = LeafNodeTbs(SelfEncPub, SelfSigPub, credential, caps,
                              MlsLeafNode.SourceCommit, parentHash, noExts,
                              (GroupId, Index));
        var sig = MlsCrypto.SignWithLabel(SelfSigD, "LeafNodeTBS", tbs);
        return MlsLeafNode.Encode(SelfEncPub, SelfSigPub, credential, caps,
                                  MlsLeafNode.SourceCommit, parentHash, noExts, sig);
    }

    static (byte[] gid, ulong epoch, byte[] treeHash, byte[] confirmedHash, byte[] extensions)
        DecodeGroupContext(byte[] gc)
    {
        var r = new TlsReader(gc);
        r.U16();                    // version
        r.U16();                    // cipher suite
        var gid = r.Bytes();
        ulong epoch = r.U64();
        var th = r.Bytes();
        var ch = r.Bytes();
        var exts = r.Bytes();
        return (gid, epoch, th, ch, exts);
    }

    // RFC 9420 §12.4.5 LeafNodeTBS: struct members inline (credential, capabilities,
    // extensions), key_package Lifetime inline, commit ParentHash opaque, and update/
    // commit sources append the leaf-node binding (group_id + leaf_index).
    static byte[] LeafNodeTbs(byte[] encKey, byte[] sigKey, byte[] credential, byte[] caps,
                              int source, byte[] sourcePayload, byte[] extensions,
                              (byte[] gid, uint index)? binding)
    {
        var w = new TlsWriter();
        w.Bytes(encKey).Bytes(sigKey).Raw(credential).Raw(caps);
        w.U8(source);
        if (source == MlsLeafNode.SourceKeyPackage) w.Raw(sourcePayload);    // Lifetime inline
        else if (source == MlsLeafNode.SourceCommit) w.Bytes(sourcePayload);  // ParentHash opaque
        w.Raw(extensions);
        if (binding != null) w.Raw(binding.Value.gid).U32(binding.Value.index);
        return w.Buf.ToArray();
    }

    static TreeKemPub CloneTree(TreeKemPub src)
    {
        var t = new TreeKemPub { Size = src.Size };
        foreach (var (k, v) in src.Nodes)
        {
            t.Nodes[k] = new MlsOptNode
            {
                IsLeaf = v.IsLeaf,
                EncKey = v.EncKey,
                SigKey = v.SigKey,
                Credential = v.Credential,
                Capabilities = v.Capabilities,
                Source = v.Source,
                SourcePayload = v.SourcePayload,
                Extensions = v.Extensions,
                Signature = v.Signature,
                ParentHash = v.ParentHash,
                Unmerged = new List<uint>(v.Unmerged),
            };
        }
        return t;
    }

    static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (var p in parts) len += p.Length;
        var b = new byte[len];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, b, o, p.Length); o += p.Length; }
        return b;
    }

    static byte[] Le64(ulong v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }
}

static class MlsRandom
{
    public static byte[] Bytes(int n)
    {
        var b = new byte[n];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }
}

static class Tls
{
    public static byte[] Bytes(byte[] v)
    {
        var w = new TlsWriter();
        w.Bytes(v);
        return w.Buf.ToArray();
    }

    public static byte[] U16(int v)
    {
        var w = new TlsWriter();
        w.U16(v);
        return w.Buf.ToArray();
    }
}
