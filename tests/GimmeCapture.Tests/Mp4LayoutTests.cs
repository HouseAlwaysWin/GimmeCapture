using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public class Mp4LayoutTests
{
    [Fact]
    public void TheIndexThenSeveralMediaAtoms_IsTheLayoutThatWalks()
    {
        Assert.True(HasIt(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20)), Atom("mdat", 40), Atom("mdat", 40), Atom("mdat", 40)));
    }

    [Fact]
    public void AFreeAtomBetween_ChangesNothing()
    {
        Assert.True(HasIt(Atom("ftyp", 12), Atom("free", 8), Atom("moov", Atom("mvhd", 20)), Atom("free", 16), Atom("mdat", 40), Atom("mdat", 40)));
    }

    [Fact]
    public void MediaDataOfASingleAtom_DoesNot()
    {
        Assert.False(HasIt(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20)), Atom("mdat", 40)));
    }

    [Fact]
    public void OneAtomOfAnotherKindAfterTheMediaData_DoesNot()
    {
        Assert.False(HasIt(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20)), Atom("mdat", 40), Atom("udta", 40)));
    }

    [Fact]
    public void AFragmentedFile_DoesNot_ItsFragmentsHaveToBeRead()
    {
        Assert.False(HasIt(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20), Atom("mvex", 16)), Atom("mdat", 40), Atom("mdat", 40)));
    }

    [Fact]
    public void AnIndexAfterTheMediaData_DoesNot_ItHasToBeFound()
    {
        Assert.False(HasIt(Atom("ftyp", 12), Atom("mdat", 40), Atom("mdat", 40), Atom("moov", Atom("mvhd", 20))));
    }

    [Fact]
    public void A64BitMediaAtom_IsRead()
    {
        Assert.True(HasIt(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20)), LargeAtom("mdat", 40), Atom("mdat", 40)));
    }

    [Fact]
    public void AnAtomRunningPastTheEndOfTheFile_DoesNot()
    {
        byte[] file = Join(Atom("ftyp", 12), Atom("moov", Atom("mvhd", 20)), Atom("mdat", 40), Atom("mdat", 40));
        Assert.False(Mp4Layout.HasIndexThenSplitMediaData(new MemoryStream(file[..^1])));
    }

    [Fact]
    public void AnotherFormat_ShortFile_AndEmptyFile_DoNot()
    {
        Assert.False(Mp4Layout.HasIndexThenSplitMediaData(new MemoryStream(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8, 9 })));
        Assert.False(Mp4Layout.HasIndexThenSplitMediaData(new MemoryStream(new byte[4])));
        Assert.False(Mp4Layout.HasIndexThenSplitMediaData(new MemoryStream()));
    }

    [Fact]
    public void AFileThatIsNotThere_DoesNot()
    {
        Assert.False(Mp4Layout.HasIndexThenSplitMediaData(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));
    }

    private static bool HasIt(params byte[][] atoms) => Mp4Layout.HasIndexThenSplitMediaData(new MemoryStream(Join(atoms)));

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Atom(string type, int size)
    {
        var atom = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)size);
        Encoding.ASCII.GetBytes(type, atom.AsSpan(4));
        return atom;
    }

    private static byte[] Atom(string type, params byte[][] children)
    {
        byte[] content = Join(children);
        var atom = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
        Encoding.ASCII.GetBytes(type, atom.AsSpan(4));
        content.CopyTo(atom, 8);
        return atom;
    }

    private static byte[] LargeAtom(string type, int size)
    {
        var atom = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(atom, 1);
        Encoding.ASCII.GetBytes(type, atom.AsSpan(4));
        BinaryPrimitives.WriteInt64BigEndian(atom.AsSpan(8), size);
        return atom;
    }
}
