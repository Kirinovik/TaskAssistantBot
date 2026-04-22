using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TaskAssistantBot.Models;
// Объект для временного хранения данных задачи
public class TaskDraft
{
    // Имя получателя
    public string RecipientName { get; set; } = "Не указано";
    // Текст задачи
    public string TaskDescription { get; set; } = "Не указано";
    // Срок 
    public string Deadline { get; set; } = "Не указано";
}
