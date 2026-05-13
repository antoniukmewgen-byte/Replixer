using FlaUI.Core.AutomationElements;

namespace Replixer.Services.Window.Detectors;

public class WhatsAppCallDetector : ICallDetector
{
    public string ProcessName => "WhatsApp.Root";

    public bool IsCallWindow(AutomationElement element)
    {
        return element.Name is
    "Аудіодзвінок" or "Відеодзвінок" or         
    "Аудіовиклик" or "Відеовиклик" or            
    "Аудиозвонок" or "Видеозвонок" or          
    "Audio call" or "Video call" or        
    "Anruf" or "Videoanruf" or               
    "Appel audio" or "Appel vidéo" or          
    "Llamada de audio" or "Llamada de video" or 
    "Połączenie audio" or "Połączenie wideo" or
    "Chiamata audio" or "Chiamata video";       
    }
}
