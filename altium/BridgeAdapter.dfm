object BridgeForm: TBridgeForm
  Left = 200
  Top = 160
  BorderStyle = bsToolWindow
  Caption = 'LCSC Bridge listener'
  ClientHeight = 132
  ClientWidth = 410
  Color = clBtnFace
  Font.Charset = DEFAULT_CHARSET
  Font.Color = clWindowText
  Font.Height = -11
  Font.Name = 'Segoe UI'
  Font.Style = []
  OldCreateOrder = False
  Position = poScreenCenter
  PixelsPerInch = 96
  TextHeight = 13
  object StatusLabel: TLabel
    Left = 16
    Top = 16
    Width = 378
    Height = 44
    AutoSize = False
    Caption = 'Ready. Requests from the component browser are processed automatically.'
    WordWrap = True
  end
  object LaunchButton: TButton
    Left = 16
    Top = 82
    Width = 122
    Height = 30
    Caption = 'Open browser'
    OnClick = LaunchButtonClick
  end
  object ProcessButton: TButton
    Left = 144
    Top = 82
    Width = 122
    Height = 30
    Caption = 'Process now'
    OnClick = ProcessButtonClick
  end
  object CloseButton: TButton
    Left = 272
    Top = 82
    Width = 122
    Height = 30
    Caption = 'Close listener'
    OnClick = CloseButtonClick
  end
  object PollTimer: TTimer
    Enabled = False
    Interval = 750
    OnTimer = PollTimerTimer
    Left = 368
    Top = 48
  end
end
