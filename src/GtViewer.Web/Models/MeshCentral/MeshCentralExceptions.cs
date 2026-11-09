namespace GtViewer.Web.Models.MeshCentral;

public class MeshCentralException : Exception
{
    public MeshCentralException(string message) : base(message) { }
    public MeshCentralException(string message, Exception innerException) : base(message, innerException) { }
}

public class MeshCentralDisconnectedException : MeshCentralException
{
    public MeshCentralDisconnectedException(string message) : base(message) { }
    public MeshCentralDisconnectedException(string message, Exception innerException) : base(message, innerException) { }
}

public class MeshCentralTimeoutException : MeshCentralException
{
    public MeshCentralTimeoutException(string message) : base(message) { }
    public MeshCentralTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}
