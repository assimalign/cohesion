$Path = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$NewLine = [System.Environment]::NewLine.ToString()
$Content = "<Project>"
$Content = $Content + $NewLine + "  <ItemGroup>"

Get-ChildItem $Path -Include *.csproj -Recurse | 
ForEach-Object {
    $Include = $_.Name.Replace(".csproj", "")
    $ProjectPath = $_.FullName.Replace($Path, "").TrimStart('\')
    $Content = $Content + $NewLine + "      <ProjectReferenceProvider Include=""$Include"" ProjectPath=""`$(RepositoryRoot)$ProjectPath"" />"
}

$Content = $Content + $NewLine + "  </ItemGroup>"
$Content = $Content + $NewLine + "  <ItemGroup>"
$Content = $Content + $NewLine + "      <Reference Update=""@(ProjectReferenceProvider)"" ProjectPath=""%(ProjectReferenceProvider.ProjectPath)"" />"
$Content = $Content + $NewLine + "      <ProjectReference Include=""@(Reference->Distinct()->'%(ProjectPath)')"" />"
$Content = $Content + $NewLine + "      <Reference Remove=""@(Reference->HasMetadata('ProjectPath'))"" />"
$Content = $Content + $NewLine + "  </ItemGroup>"
$Content = $Content + $NewLine + "</Project>"

New-Item "$Path\build\Targets\ProjectReferences.targets" -Value($Content) -Force


# <ItemGroup>
# <ProjectReferenceFinder Include="**/*.csproj" />
# </ItemGroup>
# <Target Name="NewItems">
# <CreateItem
#     Include="@(ProjectReferenceFinder->'%(Filename)')"
#     AdditionalMetadata="ProjectPath='%(ProjectReferenceFinder.FullPath)'">
#     <Output
#         TaskParameter="Include"
#         ItemName="ProjectReferenceProvider" />
# </CreateItem>
# </Target>


# <ItemGroup>
# 		<Reference Update="@(ProjectReferenceProvider)" ProjectPath="%(ProjectReferenceProvider.ProjectPath)" />
# 		<ProjectReference Include="@(Reference->Distinct()->'%(ProjectPath)')" />
# 		<Reference Remove="@(Reference->HasMetadata('ProjectPath'))" />
# 	</ItemGroup>