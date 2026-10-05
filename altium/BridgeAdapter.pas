{ Altium LCSC Bridge adapter. Target: Altium Designer 26.9.1.10. }
Interface

Type
  TBridgeForm = class(TForm)
    StatusLabel : TLabel;
    LaunchButton : TButton;
    ProcessButton : TButton;
    CloseButton : TButton;
    PollTimer : TTimer;
    procedure LaunchButtonClick(Sender: TObject);
    procedure ProcessButtonClick(Sender: TObject);
    procedure CloseButtonClick(Sender: TObject);
    procedure PollTimerTimer(Sender: TObject);
  End;

Var
  BridgeForm : TBridgeForm;
  BridgeBusy : Boolean;
  LastBridgeError : String;

Implementation

{$R *.dfm}

Procedure Fail(MessageText : String);
Begin
  LastBridgeError := MessageText;
  Raise(MessageText);
End;

Function BoolText(Value : Boolean) : String;
Begin
  If Value Then Result := 'True' Else Result := 'False';
End;

Function LocalRoot : String;
Begin
  Result := IncludeTrailingPathDelimiter(SpecialFolder_LocalApplicationData) + 'AltiumLcscBridge';
End;

Function RequestsPath : String;
Begin
  Result := LocalRoot + '\Bridge\requests\';
End;

Function ResponsesPath : String;
Begin
  Result := LocalRoot + '\Bridge\responses\';
End;

Function LogsPath : String;
Begin
  Result := LocalRoot + '\Logs\';
End;

Procedure WriteBridgeLog(LevelText, StageText, RequestId, MessageText : String);
Var Lines : TStringList; LogPath : String;
Begin
  Try
    ForceDirectories(LogsPath);
    LogPath := LogsPath + 'altium-script.log';
    Lines := TStringList.Create;
    Try
      If FileExists(LogPath) Then Lines.LoadFromFile(LogPath);
      Lines.Add(DateTimeToStr(Now) + ' [' + LevelText + '] [' + StageText + '] [request=' + RequestId + '] ' + MessageText);
      Lines.SaveToFile(LogPath);
    Finally
      Lines.Free;
    End;
  Except
  End;
End;

Procedure WriteScriptCapabilities;
Var Lines : TStringList; TempPath, FinalPath : String;
Begin
  Try
    ForceDirectories(LocalRoot + '\Bridge');
    TempPath := LocalRoot + '\Bridge\script.capabilities.tmp';
    FinalPath := LocalRoot + '\Bridge\script.capabilities';
    Lines := TStringList.Create;
    Try
      Lines.Add('protocol=1');
      Lines.Add('placement=true');
      Lines.Add('adapterVersion=0.2.4');
      Lines.SaveToFile(TempPath);
      If FileExists(FinalPath) Then DeleteFile(FinalPath);
      RenameFile(TempPath, FinalPath);
    Finally
      Lines.Free;
    End;
  Except
  End;
End;

Function ReadSetting(Name : String) : String;
Var Ini : TIniFile;
Begin
  Result := '';
  Ini := TIniFile.Create(LocalRoot + '\settings.ini');
  Try
    Result := Ini.ReadString('Bridge', Name, '');
  Finally
    Ini.Free;
  End;
End;

Function IsPathInside(ChildPath, ParentPath : String) : Boolean;
Var C, P : String;
Begin
  C := AnsiUpperCase(ExpandFileName(ChildPath));
  P := AnsiUpperCase(IncludeTrailingPathDelimiter(ExpandFileName(ParentPath)));
  Result := Pos(P, C) = 1;
End;

Procedure WriteResponse(RequestId, Status, Code, MessageText, ManifestPath : String; Modified : Boolean);
Var Lines : TStringList; TempPath, FinalPath : String;
Begin
  ForceDirectories(ResponsesPath);
  TempPath := ResponsesPath + RequestId + '.response.tmp';
  FinalPath := ResponsesPath + RequestId + '.response';
  Lines := TStringList.Create;
  Try
    Lines.Add('protocol=1');
    Lines.Add('requestId=' + RequestId);
    Lines.Add('status=' + Status);
    Lines.Add('code=' + Code);
    Lines.Add('message=' + MessageText);
    Lines.Add('manifestPath=' + ManifestPath);
    If Modified Then Lines.Add('modified=true') Else Lines.Add('modified=false');
    Lines.SaveToFile(TempPath);
    RenameFile(TempPath, FinalPath);
  Finally
    Lines.Free;
  End;
  WriteBridgeLog(Status, 'response', RequestId, Code + ': ' + MessageText);
End;

Procedure AddHiddenParameter(Component, NameText, ValueText);
Var Param : ISch_Parameter;
Begin
  If ValueText = '' Then Exit;
  Param := SchServer.SchObjectFactory(eParameter, eCreate_Default);
  If Param = Nil Then Exit;
  Param.Name := NameText;
  Param.Text := ValueText;
  Param.IsHidden := True;
  Component.AddSchObject(Param);
End;

Function EnsureFootprintLink(SchLibPath, PcbLibPath, SymbolReference, FootprintName : String; Values : TStringList) : Boolean;
Var ServerDoc : IServerDocument; CurrentLib : ISch_Lib; Component : ISch_Component; FootprintImplementation : ISch_Implementation;
    LinkMarkerPath : String;
Begin
  Result := False;
  LinkMarkerPath := SchLibPath + '.lcsc-link-v2';
  IntegratedLibraryManager.InstallLibrary(SchLibPath);
  If FileExists(LinkMarkerPath) Then Exit;

  ServerDoc := Client.OpenDocument('SCHLIB', SchLibPath);
  If ServerDoc = Nil Then Fail('Could not open schematic library for footprint assignment.');
  Client.ShowDocument(ServerDoc);
  CurrentLib := SchServer.GetCurrentSchDocument;
  If (CurrentLib = Nil) Or (CurrentLib.ObjectID <> eSchLib) Then Fail('Altium did not open the selected file as a SchLib.');
  Component := CurrentLib.GetState_SchComponentByLibRef(SymbolReference);
  If Component = Nil Then Fail('Symbol reference was not found in the SchLib: ' + SymbolReference);

  CurrentLib.CurrentSchComponent := Component;
  CurrentLib.LockViewUpdate;
  Try
    FootprintImplementation := Component.AddSchImplementation;
    If FootprintImplementation = Nil Then Fail('Altium could not create the footprint implementation.');
    FootprintImplementation.ModelName := FootprintName;
    FootprintImplementation.ModelType := 'PCBLIB';
    FootprintImplementation.Description := FootprintName;
    FootprintImplementation.IsCurrent := True;
    FootprintImplementation.UseComponentLibrary := True;
    FootprintImplementation.AddDataFileLink(FootprintName, PcbLibPath, 'PCBLIB');
    AddHiddenParameter(Component, 'Manufacturer', Values.Values['manufacturer']);
    AddHiddenParameter(Component, 'Manufacturer Part Number', Values.Values['manufacturerPartNumber']);
    AddHiddenParameter(Component, 'LCSC/JLCPCB Part Number', Values.Values['supplierPartNumber']);
    AddHiddenParameter(Component, 'Description', Values.Values['description']);
    AddHiddenParameter(Component, 'Package', Values.Values['package']);
    AddHiddenParameter(Component, 'Datasheet', Values.Values['datasheetUrl']);
    AddHiddenParameter(Component, 'Source', Values.Values['source']);
    AddHiddenParameter(Component, 'Source Revision', Values.Values['sourceRevision']);
    SchServer.RobotManager.SendMessage(Nil, c_Broadcast, SCHM_PrimitiveRegistration, FootprintImplementation.I_ObjectAddress);
  Finally
    CurrentLib.UnLockViewUpdate;
  End;
  CurrentLib.GraphicallyInvalidate;
  ServerDoc.Modified := True;
  ResetParameters;
  AddStringParameter('ObjectKind', 'Document');
  AddStringParameter('FileName', SchLibPath);
  RunProcess('WorkspaceManager:SaveObject');
  Client.CloseDocument(ServerDoc);
  With TStringList.Create Do
  Try
    Add('footprint=' + FootprintName);
    Add('pcblib=' + PcbLibPath);
    SaveToFile(LinkMarkerPath);
  Finally
    Free;
  End;
  Result := True;
End;

Procedure HandleRequest(RequestPath : String);
Var Values : TStringList; ClaimedPath, RequestId, Operation, LibraryRoot, ManifestPath : String;
    SchLibPath, PcbLibPath, IntLibPath, SymbolReference, FootprintName, TargetPath, PlacementLibrary : String;
    TargetDoc : IServerDocument; CurrentSch : ISch_Document; FoundIn : String; Modified, InIntegrated : Boolean;
Begin
  ClaimedPath := ChangeFileExt(RequestPath, '.processing');
  If Not RenameFile(RequestPath, ClaimedPath) Then Exit;
  Values := TStringList.Create;
  RequestId := ExtractFileName(ChangeFileExt(ClaimedPath, ''));
  ManifestPath := '';
  Modified := False;
  LastBridgeError := '';
  Try
    Values.LoadFromFile(ClaimedPath);
    RequestId := Values.Values['requestId'];
    WriteBridgeLog('INFO', 'request', RequestId, 'Claimed placement operation ' + Values.Values['operation'] + '.');
    ManifestPath := Values.Values['manifestPath'];
    If Values.Values['protocol'] <> '1' Then Fail('Unsupported bridge protocol.');
    Operation := Values.Values['operation'];
    If (Operation <> 'install') And (Operation <> 'installAndPlace') Then Fail('Unsupported bridge operation.');
    LibraryRoot := ReadSetting('libraryRoot');
    If LibraryRoot = '' Then Fail('Library root is not configured.');
    If Not IsPathInside(ManifestPath, LibraryRoot) Then Fail('Rejected a manifest outside the configured library root.');

    SchLibPath := Values.Values['schLibPath'];
    PcbLibPath := Values.Values['pcbLibPath'];
    IntLibPath := Values.Values['intLibPath'];
    SymbolReference := Values.Values['symbolReference'];
    FootprintName := Values.Values['footprintName'];
    TargetPath := Values.Values['targetDocument'];
    If (SchServer <> Nil) And (SchServer.GetCurrentSchDocument <> Nil) And
       (SchServer.GetCurrentSchDocument.ObjectID <> eSchLib) And (TargetPath = '') And (Client.CurrentView <> Nil) Then
      TargetPath := Client.CurrentView.GetOwnerDocument.FileName;

    If IntLibPath <> '' Then Begin
      If Not IsPathInside(IntLibPath, LibraryRoot) Then Fail('Rejected an IntLib outside the configured library root.');
      If Not FileExists(IntLibPath) Then Fail('Cached IntLib is missing.');
      IntegratedLibraryManager.InstallLibrary(IntLibPath);
      PlacementLibrary := IntLibPath;
    End Else Begin
      If Not IsPathInside(SchLibPath, LibraryRoot) Or Not IsPathInside(PcbLibPath, LibraryRoot) Then
        Fail('Rejected a source library outside the configured library root.');
      If Not FileExists(SchLibPath) Or Not FileExists(PcbLibPath) Then Fail('Cached SchLib or PcbLib is missing.');
      IntegratedLibraryManager.InstallLibrary(PcbLibPath);
      FoundIn := '';
      InIntegrated := False;
      If IntegratedLibraryManager.FindDatafileInStandardLibs(FootprintName, 'PCBLIB', PcbLibPath, InIntegrated, FoundIn) = '' Then
        Fail('Footprint was not found in the selected PcbLib: ' + FootprintName);
      WriteBridgeLog('INFO', 'verify-footprint', RequestId, 'Verified ' + FootprintName + ' in ' + PcbLibPath + '.');
      Modified := EnsureFootprintLink(SchLibPath, PcbLibPath, SymbolReference, FootprintName, Values);
      WriteBridgeLog('INFO', 'link', RequestId, 'Footprint link ready; library modified=' + BoolText(Modified) + '.');
      IntegratedLibraryManager.InstallLibrary(SchLibPath);
      PlacementLibrary := SchLibPath;
    End;

    If Operation = 'installAndPlace' Then Begin
      WriteBridgeLog('INFO', 'place', RequestId, 'Starting interactive placement; target=' + TargetPath + '.');
      If TargetPath <> '' Then Begin
        TargetDoc := Client.OpenDocument('SCH', TargetPath);
        If TargetDoc <> Nil Then Client.ShowDocument(TargetDoc);
      End;
      CurrentSch := SchServer.GetCurrentSchDocument;
      If (CurrentSch = Nil) Or (CurrentSch.ObjectID = eSchLib) Then Fail('Open a schematic sheet before placing the component.');
      If Not IntegratedLibraryManager.PlaceLibraryComponent(SymbolReference, PlacementLibrary, '') Then
        Fail('Altium rejected component placement. Check the symbol reference and installed library.');
      CurrentSch.GraphicallyInvalidate;
      WriteBridgeLog('INFO', 'place', RequestId, 'Interactive placement command accepted by Altium.');
    End;
    WriteResponse(RequestId, 'ok', 'OK', 'Altium library operation completed.', ManifestPath, Modified);
  Except
    If LastBridgeError = '' Then LastBridgeError := 'Unexpected Altium scripting error.';
    WriteBridgeLog('ERROR', 'request', RequestId, LastBridgeError);
    WriteResponse(RequestId, 'error', 'ALTIUM_ERROR', LastBridgeError, ManifestPath, Modified);
  End;
  Values.Free;
  DeleteFile(ClaimedPath);
End;

Procedure ProcessPendingRequests;
Var Files : TStringList; I : Integer;
Begin
  If BridgeBusy Then Exit;
  BridgeBusy := True;
  Files := TStringList.Create;
  Try
    ForceDirectories(RequestsPath);
    FindFiles(RequestsPath, '*.request', faAnyFile, False, Files);
    For I := 0 To Files.Count - 1 Do HandleRequest(Files[I]);
    If Files.Count > 0 Then BridgeForm.StatusLabel.Caption := IntToStr(Files.Count) + ' request(s) processed.';
  Finally
    Files.Free;
    BridgeBusy := False;
  End;
End;

Procedure LaunchBrowser;
Var AppPath : String;
Begin
  AppPath := ReadSetting('appPath');
  If (AppPath = '') Or Not FileExists(AppPath) Then Begin
    ShowError('Altium LCSC Bridge is not installed. Run installer\Install.ps1 first.');
    Exit;
  End;
  RunApplication(AppPath);
End;

Procedure TBridgeForm.LaunchButtonClick(Sender: TObject);
Begin
  LaunchBrowser;
End;

Procedure TBridgeForm.ProcessButtonClick(Sender: TObject);
Begin
  ProcessPendingRequests;
End;

Procedure TBridgeForm.CloseButtonClick(Sender: TObject);
Begin
  PollTimer.Enabled := False;
  DeleteFile(LocalRoot + '\Bridge\script.capabilities');
  WriteBridgeLog('INFO', 'shutdown', '', 'Placement listener closed.');
  BridgeForm.Close;
End;

Procedure TBridgeForm.PollTimerTimer(Sender: TObject);
Begin
  If Client.IsQuitting Then Begin PollTimer.Enabled := False; Exit; End;
  WriteScriptCapabilities;
  ProcessPendingRequests;
End;

Procedure RunLcscBrowser;
Begin
  BridgeForm.PollTimer.Enabled := False;
  BridgeBusy := False;
  BridgeForm.Show;
  WriteScriptCapabilities;
  WriteBridgeLog('INFO', 'startup', '', 'Placement listener 0.2.4 opened.');
  BridgeForm.PollTimer.Enabled := True;
  LaunchBrowser;
End;

Procedure DiagnoseBridge;
Var MessageText : String;
Begin
  MessageText := 'Altium LCSC Bridge diagnostic' + #13 +
    'Altium version target: 26.9.1.10' + #13 +
    'SchServer: ' + BoolText(SchServer <> Nil) + #13 +
    'IntegratedLibraryManager: ' + BoolText(IntegratedLibraryManager <> Nil) + #13 +
    'Library root: ' + ReadSetting('libraryRoot') + #13 +
    'Application: ' + ReadSetting('appPath');
  ShowInfo(MessageText);
End;

End.
