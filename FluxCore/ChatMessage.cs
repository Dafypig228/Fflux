using System.ComponentModel;

namespace FluxCore
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text = "";

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged("Text");
            }
        }

        public bool IsUser { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
