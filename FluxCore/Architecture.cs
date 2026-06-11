using FluxCore.LLM;

namespace FluxCore
{
    // Legacy factory for brain instance
    public class NeuralLink
    {
        public ILLMService Brain { get; private set; }

        public NeuralLink(string apiKey)
        {
            Brain = new GeminiService(apiKey);
        }
    }

    // �����-������� ��� ��������� �����
    public class OmniLoop
    {
        public SensoryCortex Cortex { get; }
        // ��� ������ ������ 'does not contain definition for Link'
        public NeuralLink Link { get; }

        public OmniLoop(SensoryCortex cortex, NeuralLink link)
        {
            Cortex = cortex;
            Link = link;
        }
    }
}