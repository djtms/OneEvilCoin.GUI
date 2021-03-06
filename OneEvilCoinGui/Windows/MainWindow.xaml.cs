﻿using Hardcodet.Wpf.TaskbarNotification;
using OneEvil.OneEvilCoinAPI;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace OneEvil.OneEvilCoinGUI.Windows
{
    public partial class MainWindow : IDisposable
    {
        public static readonly DependencyProperty TaskbarCommandsVisibilityProperty = DependencyProperty.RegisterAttached(
            "TaskbarCommandsVisibility",
            typeof(Visibility),
            typeof(MainWindow),
            new PropertyMetadata(Visibility.Visible)
        );

        public Visibility TaskbarCommandsVisibility {
            get { return (Visibility)GetValue(TaskbarCommandsVisibilityProperty); }
            set { SetValue(TaskbarCommandsVisibilityProperty, value); }
        }

        private bool IsDisposing { get; set; }

        private static OneEvilCoinClient OneEvilCoinClient { get; set; }
        private static Logger LoggerDaemon { get; set; }
        private static Logger LoggerWallet { get; set; }

        public static readonly RoutedCommand CommandShowOrHideWindow = new RoutedCommand();
        public static readonly RoutedCommand CommandSendCoins = new RoutedCommand();
        public static readonly RoutedCommand CommandShowTransactions = new RoutedCommand();

        public static readonly RoutedCommand CommandBackupManager = new RoutedCommand();
        public static readonly RoutedCommand CommandExport = new RoutedCommand();
        public static readonly RoutedCommand CommandExit = new RoutedCommand();
        public static readonly RoutedCommand CommandOptions = new RoutedCommand();
        public static readonly RoutedCommand CommandShowDebugWindow = new RoutedCommand();
        public static readonly RoutedCommand CommandShowAboutWindow = new RoutedCommand();

        private DebugWindow DebugWindow { get; set; }

        public MainWindow()
        {
            StaticObjects.MainWindow = this;

            Icon = StaticObjects.ApplicationIconImage;
            InitializeComponent();
            TaskbarIcon.Icon = StaticObjects.ApplicationIcon;

            var isAutoUpdateEnabled = true;
            string uriToOpen = null;

            // Parse command line arguments
            var arguments = Environment.GetCommandLineArgs();
            for (var i = arguments.Length - 1; i > 0; i--) {
                var argument = arguments[i];
                var argumentLower = argument.ToLower(Helper.InvariantCulture);

                if (argumentLower.StartsWith(QrUriParameters.ProtocolPreTag + ":", StringComparison.Ordinal)) {
                    uriToOpen = argument;
                    continue;
                }

                switch (argumentLower) {
                    case "-hidewindow":
                        SetTrayState(false);
                        break;

                    case "-noupdate":
                        isAutoUpdateEnabled = false;
                        break;
                }
            }

            // Register commands which can be used from the tray
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingShowOrHideWindow);
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingSendCoins);
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingShowTransactions);
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingOptions);
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingDebugWindow);
            CommandManager.RegisterClassCommandBinding(typeof(Popup), CommandBindingExit);

            OneEvilCoinClient = StaticObjects.OneEvilCoinClient;
            LoggerDaemon = StaticObjects.LoggerDaemon;
            LoggerWallet = StaticObjects.LoggerWallet;

            StartDaemon();
            SourceInitialized += delegate {
                StartWallet();

#if !DEBUG
                if (isAutoUpdateEnabled) {
                    Task.Factory.StartNew(CheckForUpdates);
                }
#endif

                if (uriToOpen != null) {
                    OpenProtocolUri(uriToOpen);
                }

                if (SettingsManager.General.IsUriAssociationCheckEnabled) {
                    Dispatcher.BeginInvoke(new Action(CheckUriAssociation));
                }
            };
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (IsDisposing) return;

            CommandShowOrHideWindow.Execute(null, this);
            e.Cancel = true;
        }

        private void CheckForUpdates()
        {
            using (var webClient = new WebClient()) {
                try {
                    // Compare the application's version with the latest one
                    var latestVersionString = webClient.DownloadString(new Uri("http://www.oneevilcoin.org/OneEvilCoin-client-net/version.txt", UriKind.Absolute));
                    if (new Version(latestVersionString + ".0").CompareTo(StaticObjects.ApplicationVersion) <= 0) return;
                    
                    var applicationBaseDirectory = StaticObjects.ApplicationBaseDirectory;

                    var updateName = "Update (v" + latestVersionString + ")";
                    var updatePath = applicationBaseDirectory + updateName;

                    // Check whether the update file has already been downloaded
                    if (!File.Exists(updatePath + ".zip")) {
                        var processorArchitectureString = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                        webClient.DownloadFile(new Uri("http://www.oneevilcoin.org/OneEvilCoin-client-net/binaries/" + processorArchitectureString + ".zip", UriKind.Absolute), updatePath + ".zip");
                    }

                    // Check whether the user wants to apply the new update now
                    if (Dispatcher.Invoke(() => this.ShowQuestion(string.Format(
                        Helper.InvariantCulture,
                        Properties.Resources.MainWindowUpdateQuestionMessage,
                        latestVersionString
                    ), Properties.Resources.MainWindowUpdateQuestionTitle)) != 1) {
                        return;
                    }
                    
                    // Extract the downloaded update
                    ZipFile.ExtractToDirectory(updatePath + ".zip", updatePath);

                    // Write a batch file which applies the update
                    using (var writer = new StreamWriter(applicationBaseDirectory + "Updater.bat")) {
                        var newLineString = Helper.NewLineString;
                        writer.Write("XCOPY /S /V /Q /R /Y \"" + updateName + "\"" + newLineString +
                                     "START \"\" \"" + StaticObjects.ApplicationAssemblyName.Name + ".exe\" -noupdate" + newLineString +
                                     "RD /S /Q \"" + updateName + "\"" + newLineString +
                                     "DEL /F /Q \"" + updateName + ".zip\"" + newLineString +
                                     "DEL /F /Q %0");
                    }

                    Dispatcher.Invoke(Application.Current.Shutdown);

                    new Process {
                        StartInfo = new ProcessStartInfo(applicationBaseDirectory + "Updater.bat") {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        }
                    }.Start();

                } catch {
                    Dispatcher.BeginInvoke(new Action(() => this.ShowError(Properties.Resources.MainWindowUpdateError)));
                }
            }
        }

        public void SetTrayState(bool isWindowVisible)
        {
            string bindingPath;

            if (isWindowVisible) {
                Visibility = Visibility.Visible;
                bindingPath = "MenuHideWindow";
                this.RestoreWindowStateFromMinimized();

            } else {
                Visibility = Visibility.Hidden;
                bindingPath = "MenuShowWindow";
            }

            MenuItemShowOrHideWindow.SetBinding(
                HeaderedItemsControl.HeaderProperty,
                new Binding {
                    Path = new PropertyPath(bindingPath),
                    Source = CultureManager.ResourceProvider,
                    Mode = BindingMode.OneWay
                }
            );
        }

        public void OpenProtocolUri(string input)
        {
            var uriParts = ConverterUriPartArrayToUriString.Provider.ConvertBack(input, new[] { typeof(string) }, null, Helper.InvariantCulture);
            if (uriParts.Length == 0) return;

            var firstUriPart = uriParts[0] as string;
            Debug.Assert(firstUriPart != null, "firstUriPart != null");
            var address = firstUriPart.Substring(QrUriParameters.ProtocolPreTag.Length + 1);

            string paymentId = null;
            ulong amount = 0;
            string label = null;
            string message = null;
            
            // Parse values from each uri part
            for (var i = uriParts.Length - 1; i >= 0; i--) {
                var uriPart = uriParts[i] as string;
                Debug.Assert(uriPart != null, "uriPart != null");

                var uriPartSplit = uriPart.Split(new[] { '=' }, 2);

                switch (uriPartSplit[0]) {
                    case QrUriParameters.PaymentId:
                        paymentId = uriPartSplit[1];
                        break;

                    case QrUriParameters.Amount:
                        ulong value;
                        if (ulong.TryParse(uriPartSplit[1], out value)) amount = value;
                        break;

                    case QrUriParameters.Label:
                        label = uriPartSplit[1];
                        break;

                    case QrUriParameters.Message:
                        message = uriPartSplit[1];
                        break;
                }
            }

            // Join all payment details into a string
            var paymentDetailsSummary = string.Empty;
            if (!string.IsNullOrEmpty(message)) {
                paymentDetailsSummary += Helper.NewLineString + Properties.Resources.TextMessage + " " + message;
            }
            if (!string.IsNullOrEmpty(address)) {
                paymentDetailsSummary += Helper.NewLineString + Properties.Resources.TextAddress + Properties.Resources.PunctuationColon + " " + address;
            }
            if (!string.IsNullOrEmpty(paymentId)) {
                paymentDetailsSummary += Helper.NewLineString + Properties.Resources.TextPaymentId + " " + paymentId;
            }
            if (amount > 0) {
                paymentDetailsSummary += Helper.NewLineString + Properties.Resources.TextAmount + Properties.Resources.PunctuationColon + " " + amount;
            }
            if (!string.IsNullOrEmpty(label)) {
                paymentDetailsSummary += Helper.NewLineString + Properties.Resources.TextLabel + Properties.Resources.PunctuationColon + " " + label;
            }

            // Don't show empty URI requests
            if (paymentDetailsSummary.Length == 0) return;

            Dispatcher.BeginInvoke(new Action(() => {
                // Ask the user whether opening the URI is wanted
                if (this.ShowQuestion(
                    Properties.Resources.MainWindowUriOpenQuestionMessage1 + Helper.NewLineString +
                    paymentDetailsSummary + Helper.NewLineString + Helper.NewLineString +
                    string.Format(Helper.InvariantCulture, Properties.Resources.MainWindowUriOpenQuestionMessage2, Properties.Resources.MainWindowSendCoins),
                    Properties.Resources.MainWindowUriOpenQuestionTitle
                ) != 1) {
                    return;
                }

                SendCoinsView.ClearRecipients();

                var recipient = new SendCoinsRecipient(SendCoinsView) { Address = address, Amount = amount, Label = label };
                SendCoinsView.ViewModel.Recipients[0] = recipient;
                SendCoinsView.ViewModel.PaymentId = paymentId;

                CommandSendCoins.Execute(null, this);
            }));
        }

        private void CheckUriAssociation()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32)) {
                using (var subKeyMain = baseKey.CreateSubKey("OneEvilCoin")) {
                    if (subKeyMain == null) return;

                    using (var subKeyCommand = subKeyMain.CreateSubKey(@"shell\open\command")) {
                        if (subKeyCommand == null) return;

                        var applicationPath = StaticObjects.ApplicationPath;
                        var commandString = subKeyCommand.GetValue(null) as string;
                        if (commandString != null) {
                            if (commandString.Contains(applicationPath)) {
                                // The current application is already the default handler of URIs
                                return;
                            }

                            // The current application is not the default handler of URIs
                            switch (MessageBoxEx.Show(
                                this,
                                Properties.Resources.MainWindowUriAssociationQuestionTitle,
                                string.Format(Helper.InvariantCulture, Properties.Resources.MainWindowUriAssociationQuestionMessage, QrUriParameters.ProtocolPreTag + ":"),
                                SystemIcons.Question,
                                Properties.Resources.TextYes,
                                Properties.Resources.TextNo,
                                Properties.Resources.TextDoNotAskAgain
                            )) {
                                case 2:
                                    // No
                                    return;

                                case 3:
                                    // Don't ask again
                                    SettingsManager.General.IsUriAssociationCheckEnabled = false;
                                    return;
                            }
                        }

                        // Register the currency's protocol
                        subKeyMain.SetValue(null, "URL:" + QrUriParameters.ProtocolPreTag.UppercaseFirst() + " Protocol", RegistryValueKind.String);
                        subKeyMain.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
                        subKeyCommand.SetValue(null, "\"" + applicationPath + "\" \"%1\"", RegistryValueKind.String);
                        using (var subKeyDefaultIcon = subKeyMain.CreateSubKey("DefaultIcon")) {
                            if (subKeyDefaultIcon == null) return;
                            subKeyDefaultIcon.SetValue(null, "\"" + StaticObjects.ApplicationAssemblyName.Name + ".exe\"");
                        }
                    }
                }
            }
        }

        private void CommandShowOrHideWindow_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SetTrayState(Visibility != Visibility.Visible);
        }

        private void CommandSendCoins_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TabItemSendCoins.IsSelected) {
                this.SetFocusedElement(TabItemSendCoins);
            }

            this.RestoreWindowStateFromMinimized();
        }

        private void CommandShowTransactions_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!TabItemTransactions.IsSelected) {
                this.SetFocusedElement(TabItemTransactions);
            }

            this.RestoreWindowStateFromMinimized();
        }

        private void CommandBackupManager_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayDialog(new BackupManagerWindow(this));
        }

        private void CommandExport_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Debug.Assert(TabControl.SelectedContent as IExportable != null, "TabControl.SelectedContent as IExportable != null");
            (TabControl.SelectedContent as IExportable).Export();
        }

        private void CommandExit_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            StaticObjects.IsUnhandledExceptionLoggingEnabled = false;

            if (SettingsManager.General.IsSafeShutdownEnabled) {
                if (IsDisposing) return;

                Visibility = Visibility.Hidden;

                TaskbarIcon.DoubleClickCommand = null;
                TaskbarIcon.ContextMenu = null;
                TaskbarIcon.ToolTipText = Properties.Resources.TaskbarShutdown;

                TaskbarIcon.ShowBalloonTip(Properties.Resources.TaskbarShutdown,
                                           Properties.Resources.TaskbarDoNotInterruptProcess,
                                           BalloonIcon.Info);

                Task.Factory.StartNew(Dispose);
                
            } else {
                IsDisposing = true;
                Application.Current.Shutdown();
            }
        }

        private void CommandOptions_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayDialog(new OptionsWindow(this));

            // This is needed for the behavior of the tray icon
            if (WindowState != WindowState.Minimized) this.ActivateWindowOrLastChild();
        }

        private void CommandShowDebugWindow_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (DebugWindow == null) {
                DebugWindow = new DebugWindow();
                DebugWindow.Closed += delegate { DebugWindow = null; };
                DebugWindow.Show();

            } else {
                DebugWindow.Activate();
            }
        }

        private void CommandShowAboutWindow_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayDialog(new AboutWindow(this));
        }

        private void CommandExport_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = TabControl.SelectedContent is IExportable;
        }

        private void StartDaemon()
        {
            var daemon = OneEvilCoinClient.Daemon;
            daemon.OnLogMessage += Daemon_OnLogMessage;
            daemon.NetworkInformationChanging += Daemon_NetworkInformationChanging;
            daemon.BlockchainSynced += Daemon_BlockchainSynced;

            daemon.Start();
        }

        private void StartWallet()
        {
            var wallet = OneEvilCoinClient.Wallet;
            wallet.OnLogMessage += Wallet_OnLogMessage;
            wallet.PassphraseRequested += Wallet_PassphraseRequested;
            wallet.AddressReceived += Wallet_AddressReceived;
            wallet.TransactionReceived += Wallet_TransactionReceived;
            wallet.BalanceChanging += Wallet_BalanceChanging;

            wallet.Start();

            OverviewView.ViewModel.DataSourceTransactions = wallet.Transactions;
            TransactionsView.ViewModel.DataSourceTransactions = wallet.Transactions;
        }

        private static void Daemon_OnLogMessage(object sender, string e)
        {
            LoggerDaemon.Log(e);
        }

        private void Daemon_NetworkInformationChanging(object sender, NetworkInformationChangingEventArgs e)
        {
            var newValue = e.NewValue;

            var connectionCount = newValue.ConnectionCountTotal;
            var blockTimeRemainingString = newValue.BlockTimeRemaining.ToStringReadable();

            var syncBarProgressPercentage = (double)newValue.BlockHeightDownloaded / newValue.BlockHeightTotal;
            var syncBarText = string.Format(
                Helper.InvariantCulture,
                Properties.Resources.StatusBarSyncTextMain,
                newValue.BlockHeightRemaining,
                blockTimeRemainingString
            );
            var syncStatusText = string.Format(
                Helper.InvariantCulture,
                Properties.Resources.StatusBarStatusTextMain,
                newValue.BlockHeightDownloaded,
                newValue.BlockHeightTotal,
                (syncBarProgressPercentage * 100).ToString("F2", CultureManager.CurrentCulture),
                blockTimeRemainingString
            );

            BeginInvokeForDataChanging(() => {
                var statusBarViewModel = StatusBar.ViewModel;

                statusBarViewModel.ConnectionCount = connectionCount;
                statusBarViewModel.SyncBarProgressPercentage = syncBarProgressPercentage;
                statusBarViewModel.SyncBarText = syncBarText;
                statusBarViewModel.SyncStatusText = syncStatusText;
                statusBarViewModel.SyncStatusSynchronizingVisibility = Visibility.Visible;
            });
        }

        private void Daemon_BlockchainSynced(object sender, EventArgs e)
        {
            BeginInvokeForDataChanging(() => {
                // Enable sending coins, along with hiding the sync status
                StatusBar.ViewModel.SyncStatusSynchronizingVisibility = Visibility.Hidden;
                SendCoinsView.ViewModel.IsBlockchainSynced = true;
            });
        }

        private static void Wallet_OnLogMessage(object sender, string e)
        {
            LoggerWallet.Log(e);
        }

        private void Wallet_PassphraseRequested(object sender, PassphraseRequestedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (e.IsFirstTime) {
                    // Let the user set the wallet's passphrase for the first time
                    var dialog = new WalletChangePassphraseWindow(this, false);
                    if (DisplayDialog(dialog) == true) {
                        OneEvilCoinClient.Wallet.Passphrase = dialog.NewPassphrase;
                    } else {
                        OneEvilCoinClient.Wallet.Passphrase = null;
                    }

                } else {
                    // Request the wallet's passphrase in order to unlock it
                    var dialog = new WalletUnlockWindow(this);
                    if (DisplayDialog(dialog) == true) {
                        OneEvilCoinClient.Wallet.Passphrase = dialog.Passphrase;
                    }
                }
            }));
        }

        private void Wallet_AddressReceived(object sender, AddressReceivedEventArgs e)
        {
            BeginInvokeForDataChanging(() => OverviewView.ViewModel.Address = e.Address);
        }

        private void Wallet_TransactionReceived(object sender, TransactionReceivedEventArgs e)
        {
            var transaction = e.Transaction;

            string balloonTitle;
            switch (transaction.Type) {
                case TransactionType.Receive:
                    balloonTitle = Properties.Resources.TaskbarTransactionIncoming;
                    break;

                case TransactionType.Send:
                    balloonTitle = Properties.Resources.TaskbarTransactionOutgoing;
                    break;

                default:
                    balloonTitle = Properties.Resources.TaskbarTransactionUnknownType;
                    break;
            }

            var balloonMessageExtra = string.Empty;
            if (transaction.Type == TransactionType.Unknown) {
                balloonMessageExtra = Properties.Resources.TransactionsSpendable + Properties.Resources.PunctuationColon + " " +
                                      Dispatcher.Invoke(() => ConverterBooleanToString.Provider.Convert(transaction.IsAmountSpendable, typeof(string), null, Helper.InvariantCulture)) + Helper.NewLineString;
            }

            var amountDisplayValue = transaction.Amount / StaticObjects.CoinAtomicValueDivider;
            var balloonMessage = Properties.Resources.TextAmount + Properties.Resources.PunctuationColon + " " + amountDisplayValue.ToString(StaticObjects.StringFormatCoinDisplayValue, Helper.InvariantCulture) + " " + Properties.Resources.TextCurrencyCode + Helper.NewLineString +
                                 balloonMessageExtra +
                                 Helper.NewLineString +
                                 Properties.Resources.TransactionsTransactionId + Properties.Resources.PunctuationColon + " " + transaction.TransactionId;

            Dispatcher.BeginInvoke(new Action(() => TaskbarIcon.ShowBalloonTip(balloonTitle, balloonMessage, BalloonIcon.Info)));
        }

        private void Wallet_BalanceChanging(object sender, BalanceChangingEventArgs e)
        {
            var newValue = e.NewValue;

            BeginInvokeForDataChanging(() => {
                var overviewViewModel = OverviewView.ViewModel;
                overviewViewModel.BalanceSpendable = newValue.Spendable;
                overviewViewModel.BalanceUnconfirmed = newValue.Unconfirmed;

                SendCoinsView.ViewModel.BalanceSpendable = newValue.Spendable;
            });
        }

        private bool? DisplayDialog(Window window)
        {
            TaskbarCommandsVisibility = Visibility.Collapsed;
            var output = window.ShowDialog();
            TaskbarCommandsVisibility = Visibility.Visible;

            return output;
        }

        private void BeginInvokeForDataChanging(Action callback)
        {
            Dispatcher.BeginInvoke(callback, DispatcherPriority.DataBind);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing && !IsDisposing) {
                IsDisposing = true;
                
                if (OneEvilCoinClient != null) {
                    OneEvilCoinClient.Dispose();
                }

                Dispatcher.Invoke(Application.Current.Shutdown);
            }
        }
    }
}
