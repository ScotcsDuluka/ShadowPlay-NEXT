using System.Security.Cryptography;

/// <summary>
/// ISOLATED TEST-ONLY, native Media Foundation acceptance + atomic promotion.
/// Does not call FFmpeg or any external executable. No runtime deployment.
/// The test's "verified" artifact is not Production RecordingSession Saved.
/// </summary>
internal static class NativeVerifiedEvidencePublisher
{
    public static string Publish(string stage,string verified,Action<bool,string> check)
    {
        string stagePath=Path.GetFullPath(stage);
        string destPath=Path.GetFullPath(verified);
        if(string.Equals(stagePath,destPath,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(Path.GetDirectoryName(stagePath),
               Path.GetDirectoryName(destPath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("native_av_same_directory_atomic_publish_required");
        if(!File.Exists(stagePath)||File.Exists(destPath))
            throw new IOException("native_av_stage_missing_or_destination_exists");
        using var file=new FileStream(stagePath,FileMode.Open,FileAccess.Read,
            FileShare.None);
        if(file.Length<1000 || file.Length>12_000_000)
            throw new InvalidDataException("native_av_stage_invalid_size");
        byte[] sourceHash=SHA256.HashData(file);
        file.Close();
        // The native decoder verifies both tracks to EOS. Any failure leaves
        // the original stage and no Verified destination.
        NativeMfTwoTrackDecodeEvidence.Verify(stagePath,(ok,msg)=>{
            if(!ok)throw new InvalidDataException("native_av_decode_rejected:"+msg);
        });
        File.Move(stagePath,destPath); // same-volume rename, no overwrite
        try
        {
            byte[] finalHash=SHA256.HashData(File.ReadAllBytes(destPath));
            if(!CryptographicOperations.FixedTimeEquals(sourceHash,finalHash))
                throw new InvalidDataException("native_av_published_bytes_changed");
            check(!File.Exists(stagePath)&&File.Exists(destPath),
                "verified native A/V file appears only after complete native two-track decode");
            check(true,"native same-directory atomic promotion preserves exact MP4 SHA256");
            Console.WriteLine("AORUS_NATIVE_AV_TEST_VERIFIED_SHA256="+
                Convert.ToHexString(sourceHash));
            return destPath;
        }
        catch
        {
            // An unexpected verification problem must not leave a VERIFIED
            // artifact. Never touch any unrelated user or Production files.
            if(File.Exists(destPath))File.Move(destPath,stagePath);
            throw;
        }
    }
}
