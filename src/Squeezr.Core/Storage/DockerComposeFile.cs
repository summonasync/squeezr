namespace Squeezr.Core.Storage;

public static class DockerComposeFile
{
    private const string Content =
        """
        services:
          qdrant:
            image: qdrant/qdrant:latest
            ports: ["6333:6333", "6334:6334"]
            volumes:
              - squeezr-qdrant:/qdrant/storage
        volumes:
          squeezr-qdrant:
        """;

    public static void Write(string root) => File.WriteAllText(SqueezrPaths.ComposePath(root), Content);
}
