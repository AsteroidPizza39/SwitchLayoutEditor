namespace BflytPreview.Compression
{
	/// <summary>
	/// How an archive was wrapped on disk (TotK uses ZSTD .zs; Switch themes often use Yaz0 .szs).
	/// </summary>
	public enum ArchiveCompression
	{
		None = 0,
		Yaz0 = 1,
		Zstd = 2
	}
}
