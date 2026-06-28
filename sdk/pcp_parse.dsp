# Microsoft Developer Studio Project File - Name="pcp_parse" - Version 6.00
# Small utility project to parse PCP headers

Header:
	ProjectName = "pcp_parse"
	ConfigurationType = 1
EndHeader

Config(Debug|Win32):
	ExtraPreprocessorDefinitions = _DEBUG;WIN32
	OutputDirectory = Debug
	IntermediateDirectory = Debug
EndConfig

Config(Release|Win32):
	ExtraPreprocessorDefinitions = NDEBUG;WIN32
	OutputDirectory = Release
	IntermediateDirectory = Release
EndConfig

File = "pcp_parse.c"
	FileType = 1
	FileSubType = 0
EndFile