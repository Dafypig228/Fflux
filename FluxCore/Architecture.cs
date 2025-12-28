namespace FluxCore
{
    // Класс-обертка для связи с мозгом
    public class NeuralLink
    {
        public GeminiService Brain { get; private set; }

        public NeuralLink(string apiKey)
        {
            Brain = new GeminiService(apiKey);
        }
    }

    // Класс-обертка для основного цикла
    public class OmniLoop
    {
        public SensoryCortex Cortex { get; }
        // ЭТО РЕШАЕТ ОШИБКУ 'does not contain definition for Link'
        public NeuralLink Link { get; }

        public OmniLoop(SensoryCortex cortex, NeuralLink link)
        {
            Cortex = cortex;
            Link = link;
        }
    }
}