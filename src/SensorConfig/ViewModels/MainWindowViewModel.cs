using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using LibreHwAccess;
using SensorConfig.Models;

namespace SensorConfig.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly LibreHwReader _reader = new();
        private ObservableCollection<LibreSensorReading> _availableSensors = new();
        private ObservableCollection<SelectedSensorModel> _selectedSensors = new();
        private LibreSensorReading? _selectedAvailableSensor;
        private SelectedSensorModel? _selectedMapping;
        private string _aliasInput = string.Empty;
        private string _searchText = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _isAvailable;
        private ObservableCollection<LibreSensorReading> _filteredSensors = new();
        private System.Timers.Timer? _refreshTimer;

        public MainWindowViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh);
            SaveCommand = new RelayCommand(Save, () => SelectedSensors.Count > 0);
            AddMappingCommand = new RelayCommand(AddMapping, () => SelectedAvailableSensor != null && !string.IsNullOrWhiteSpace(AliasInput));
            RemoveMappingCommand = new RelayCommand(RemoveMapping, () => SelectedMapping != null);

            LoadConfig();
            Refresh();
            StartAutoRefresh();
        }

        public ObservableCollection<LibreSensorReading> AvailableSensors
        {
            get => _availableSensors;
            set { _availableSensors = value; OnPropertyChanged(); ApplyFilter(); }
        }

        public ObservableCollection<SelectedSensorModel> SelectedSensors
        {
            get => _selectedSensors;
            set { _selectedSensors = value; OnPropertyChanged(); }
        }

        public ObservableCollection<LibreSensorReading> FilteredSensors
        {
            get => _filteredSensors;
            set { _filteredSensors = value; OnPropertyChanged(); }
        }

        public LibreSensorReading? SelectedAvailableSensor
        {
            get => _selectedAvailableSensor;
            set
            {
                _selectedAvailableSensor = value;
                OnPropertyChanged();
                ((RelayCommand)AddMappingCommand).RaiseCanExecuteChanged();
            }
        }

        public SelectedSensorModel? SelectedMapping
        {
            get => _selectedMapping;
            set
            {
                _selectedMapping = value;
                OnPropertyChanged();
                ((RelayCommand)RemoveMappingCommand).RaiseCanExecuteChanged();
            }
        }

        public string AliasInput
        {
            get => _aliasInput;
            set
            {
                _aliasInput = value;
                OnPropertyChanged();
                ((RelayCommand)AddMappingCommand).RaiseCanExecuteChanged();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public bool IsAvailable
        {
            get => _isAvailable;
            set { _isAvailable = value; OnPropertyChanged(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand AddMappingCommand { get; }
        public ICommand RemoveMappingCommand { get; }

        private void StartAutoRefresh()
        {
            _refreshTimer = new System.Timers.Timer(2000);
            _refreshTimer.Elapsed += (s, e) => RefreshAvailableSensors();
            _refreshTimer.AutoReset = true;
            _refreshTimer.Start();
        }

        private void LoadConfig()
        {
            try
            {
                string path = SensorMappingConfig.DefaultConfigPath;
                var loaded = SensorMappingConfig.Load(path);
                SelectedSensors = loaded;
                StatusMessage = $"{loaded.Count} mapping(s) loaded";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading config: {ex.Message}";
            }
        }

        private async void Refresh()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    IsAvailable = _reader.IsAvailable();
                    RefreshAvailableSensors();
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error: {ex.Message}";
                }
            });
        }

        private void RefreshAvailableSensors()
        {
            try
            {
                if (!_reader.IsAvailable())
                {
                    IsAvailable = false;
                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        AvailableSensors = new ObservableCollection<LibreSensorReading>();
                        StatusMessage = "LibreHardwareMonitor not available (run as Admin?)";
                    });
                    return;
                }

                IsAvailable = true;
                var sensors = _reader.ReadAllSensors();
                App.Current?.Dispatcher.Invoke(() =>
                {
                    AvailableSensors = new ObservableCollection<LibreSensorReading>(sensors);
                    StatusMessage = $"{sensors.Count} sensor(s) found";

                    UpdateSelectedSensorLiveValues(sensors);
                });
            }
            catch
            {
                // Timer callback swallows exceptions
            }
        }

        private void UpdateSelectedSensorLiveValues(System.Collections.Generic.List<LibreSensorReading> allSensors)
        {
            foreach (var sel in SelectedSensors)
            {
                var match = allSensors.FirstOrDefault(s =>
                    s.LabelOrig == sel.LabelOrig &&
                    s.DeviceName == sel.DeviceName &&
                    (string.IsNullOrEmpty(sel.ReadingType) || s.ReadingType == sel.ReadingType));

                if (match != null)
                {
                    sel.CurrentValue = match.Value;
                    sel.Unit = match.Unit;
                }
            }
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                FilteredSensors = new ObservableCollection<LibreSensorReading>(AvailableSensors);
                return;
            }

            var filtered = AvailableSensors
                .Where(s =>
                    s.DeviceName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.LabelOrig.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.LabelUser.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            FilteredSensors = new ObservableCollection<LibreSensorReading>(filtered);
        }

        private void AddMapping()
        {
            if (SelectedAvailableSensor == null || string.IsNullOrWhiteSpace(AliasInput))
                return;

            string alias = AliasInput.Trim();

            if (SelectedSensors.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"Alias '{alias}' already exists";
                return;
            }

            var model = new SelectedSensorModel
            {
                Alias = alias,
                LabelOrig = SelectedAvailableSensor.LabelOrig,
                DeviceName = SelectedAvailableSensor.DeviceName,
                ReadingType = SelectedAvailableSensor.ReadingType,
                CurrentValue = SelectedAvailableSensor.Value,
                Unit = SelectedAvailableSensor.Unit,
            };

            SelectedSensors.Add(model);
            AliasInput = string.Empty;
            StatusMessage = $"Added '{alias}'";
            ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
        }

        private void RemoveMapping()
        {
            if (SelectedMapping == null)
                return;

            string alias = SelectedMapping.Alias;
            SelectedSensors.Remove(SelectedMapping);
            SelectedMapping = null;
            StatusMessage = $"Removed '{alias}'";
            ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
        }

        private void Save()
        {
            try
            {
                string path = SensorMappingConfig.DefaultConfigPath;
                SensorMappingConfig.Save(SelectedSensors, path);
                StatusMessage = $"Saved {SelectedSensors.Count} mapping(s) to {path}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving: {ex.Message}";
            }
        }

        public void Stop()
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _reader.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter)
        {
            _execute();
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
