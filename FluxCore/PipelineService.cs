using System;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore
{
    public class PipelineService
    {
        private readonly ILLMService _llm;
        private readonly ContextService _context;
        private readonly ScreenService _screen;

        // "�������� ������" - ��������� ��������� ����
        public string CurrentWorldState { get; private set; } = "������� ��������. �������� ������.";

        public PipelineService(ILLMService llm, ContextService context, ScreenService screen)
        {
            _llm = llm;
            _context = context;
            _screen = screen;
        }

        // ���� ����� ���������� ������ 2-3 ������� ��� �� �������� (����)
        public async Task UpdateWorldState()
        {
            // 1. ���� ���������� (Delta Check)
            string layer1 = _context.GetLayer1_Metadata(out bool appChanged);

            // 2. ���� ������ + ������ (Delta Check)
            var layer2Result = await _screen.GetLayer2_OCR_WithDelta();

            // ���� �� ����������, �� �������� �� ���������� - ����.
            if (!appChanged && !layer2Result.VisualChanged)
            {
                // �� �� ������� ��, �������� �������, ���������� ������ WorldState
                return;
            }

            string layer2 = layer2Result.Text;
            if (layer2.Length > 1500) layer2 = layer2.Substring(0, 1500) + "...";

            // 3. ��������� ������ "����������"
            // �� ������ Gemini ��������� ���� ��� ����� "Local Model", ������� ������� ������ � ����.
            string abstractionPrompt = $@"
����: �� - ������ ���������� (Data Abstraction Layer).
���� ������ ���������� ����� ������ � �������� � ������� ������ �������� (World State).

������� ������:
[����������]: {layer1}
[������� �����]: {layer2}

������:
����� ����� �������, ��� ���������� �� ������.
������: ""������������ � Chrome ������ ������ ��� React Hooks.""
������: ""������������ � Visual Studio ����������� ����� Program.cs.""
����� (������ ������ ��������):";

            // �������� ����� "������ ����"
            // ���������� Gemini ��� ������� ��������� ������
            CurrentWorldState = await _llm.AskSimple(abstractionPrompt);
        }

        public async Task<string> ExecuteUserQuery(string userVoice)
        {
            // 4. ���� UI (���������� � ������ �������)
            string layer3 = _context.GetLayer3_UIHierarchy();

            // ��������� ������ � ��
            // � ���� ���� "������ ����" (�� ������) + "���� � ����" (Layer 3) + ������
            string prompt = $@"
STATE: {CurrentWorldState}
FOCUS: {layer3}
USER: ""{userVoice}""

������ �� ��������� (STATE) � ����, �� ��� ��������� ������������ (FOCUS), ������ �� ������.";

            return await _llm.AskSimple(prompt);
        }
    }
}