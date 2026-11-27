namespace NISS.Monitor.Protocol;

/// <summary>
/// Publisher identity pin: a file passes if it is Authenticode-signed by a chain to ANY accepted subject.
/// More than one subject = dual-trust during a certificate rotation window. Never a leaf thumbprint.
/// </summary>
public sealed record PublisherPin(IReadOnlyList<string> AcceptedSubjects);

/// <summary>Authenticode check; the Windows implementations live in the server and agent projects.</summary>
public interface ISignatureChecker
{
    bool IsAuthenticodeSignedBy(byte[] fileBytes, PublisherPin pin);
}
