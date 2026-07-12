using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LibreHwAccess;
using Newtonsoft.Json;

namespace SensorConfig.Models
{
    public class SelectedSensorModel : INotifyPropertyChanged
    {
        private string _alias = string.Empty;
        public string Alias
        {
            get => _alias;
            set { _alias = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
        }

        public string LabelOrig { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string ReadingType { get; set; } = string.Empty;

        private double _currentValue;
        public double CurrentValue
        {
            get => _currentValue;
            set { _currentValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
        }

        public string Unit { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayText => $"{Alias}  ({DeviceName} / {LabelOrig})";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SensorConfigRoot
    {
        [JsonProperty("selectedSensors")]
        public ObservableCollection<SelectedSensorModel> SelectedSensors { get; set; } = new();
    }

    public static class SensorMappingConfig
    {
        public static string DefaultConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TurzxSensorBridge");
                return Path.Combine(dir, "selected_sensors.json");
            }
        }

        public static void Save(ObservableCollection<SelectedSensorModel> sensors, string path)
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var root = new SensorConfigRoot { SelectedSensors = sensors };
            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        public static ObservableCollection<SelectedSensorModel> Load(string path)
        {
            if (!File.Exists(path))
                return new ObservableCollection<SelectedSensorModel>();

            string json = File.ReadAllText(path);
            var root = JsonConvert.DeserializeObject<SensorConfigRoot>(json);
            return root?.SelectedSensors ?? new ObservableCollection<SelectedSensorModel>();
        }
    }
}
