public static class Paths
{
    public static FilePath SolutionFile =>  "Linker.sln";
    public static FilePath WebProjectFile => $"{FrontendDirectory}/Linker.csproj";
    public static DirectoryPath FrontendDirectory => "src/Linker";
    public static DirectoryPath PublishDirectory => "publish";
}