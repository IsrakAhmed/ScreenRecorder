   M S B A M L   `   `   `  ����  ScreenRecorder    ScreenRecorder.MainWindow   5   	   W SPresentationCore, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35e aSystem.Windows.Controls.Ribbon, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089R NWindowsBase, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35^ ZWindowsFormsIntegration, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35H 9http://schemas.microsoft.com/winfx/2006/xaml/presentation      5   	   R NSystem.Xaml, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5   	     � Title$  Screen Recorder��5   	   $	��400��6!   $	��600��6.   .��5      � $��10q�6    � RowDefinitions 5   
   �� 5      $
��Auto �6   �� 5      $
��Auto �6   �� 5	      $
��Auto �6   �� 5
      $
��Auto �6   �� 5      $��* �6    � ColumnDefinitions 5   
   �� 5      $
��Auto �6   �� 5      $��* �6   .+�5   
   �� $��Output Path:��6   $��0��6'   $��0��64   $��Center=�6D   �� 5   
   -   5         ��Name$ txtOutputPath��5      $��0��6)   $��1��66    �� 
IsReadOnly$
 True��6F   $��5q�6X   �� 5   
   -   5         ��Name$ 	BtnBrowse��5      $��	Browse...��6$   $��1��68   $��1��6E   $	��100��6U   $
��Left=�6a   $��5q�6|   6�   �� 5   
   $��Audio Source:��6   $��2��6(   $��0��65   $��Center=�6E   �� 5   
   -   5         ��Name$ cmbAudioSourceType��5      $��2��6/   $��1��6<   $��5q�6L   .@�5      �� 
 � Tag$
 none��6   .��6&   
No Audio60   �� 5      $ system��6   .��6(   System Audio66   �� 5      $	 mic��6   .��6%   
Microphone61   �� 5      $
 both��6   .��6&   System + Microphone6;   5      �� 5   
   $��Audio Device:��6   $��3��6(   $��0��65   $��Center=�6E   �� 5   
   -   5        $ cmbAudioDevices��5      $��3��6,   $��1��69   $��5q�6I   ${�False��6T   �� 5!   
   $��4��6   $��1��6"    �� Orientation$ 
Horizontal=�62   $
��Left=�6K   $��5q�6f   .+�5"      �� -   5        $ btnStart��5"      $��	⏺ Start��6'   $	��100��69   $��5q�6E   6P   �� 5#      -   5        $ btnPause��5#      $��	⏸ Pause��6'   $	��100��69   $��5q�6E   ${�False��6g   �� 5$      -   5        $ btnStop��5$      $��⏹ Stop��6&   $	��100��67   $��5q�6C   ${�False��6d   5%      �� 5&   
   -   5        	 ��Name$	 
txtElapsed��5&      $��4��6(   $��0��65   $��Time Elapsed: 00:00:00��6E   $	��Top=�6c   $��5q�6{   5'      5(      