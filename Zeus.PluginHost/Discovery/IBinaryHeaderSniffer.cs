// IBinaryHeaderSniffer.cs — file-header magic-byte detector.
//
// Single-method seam so unit tests can swap a fake in for the scanner. The
// concrete implementation reads only the first ~4 KiB of a file and never
// loads any code; it must be safe to invoke against arbitrary executables.

namespace Zeus.PluginHost.Discovery;

public interface IBinaryHeaderSniffer
{
    SniffResult Sniff(string filePath);
}
