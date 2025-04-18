using Microsoft.AspNetCore.Http;
using Contracts.Constants;

public class FieldsConfig
{
    public List<string> MandatoryFields { get; set; } = new();
    public List<List<string>> AlternativeFields { get; set; } = new();
    public List<string> OptionalFields { get; set; } = new();
}

public class TaskRequest
{
    public Guid Id { get; set; }= Guid.NewGuid(); 
    public string Task { get; set; }
    public Guid? RequesterId { get; set; }

    public Dictionary<string, object> Kwargs { get; set; }
    public Dictionary<string, object> PrivateArgs { get; set; }

    private Dictionary<string, FieldsConfig> Fields =
        new()
        {
            {
                AvailableModels.SpeechToText,
                new FieldsConfig
                {
                    MandatoryFields = new List<string> { "input" },
                    AlternativeFields = new List<List<string>> { },
                    OptionalFields = new List<string> { "model", "webvtt" },
                }
            },
        };

    public static async Task<TaskRequest> Create(IFormCollection form)
    {
        var request = new TaskRequest();
        request.Task = form["task"];
        request.Kwargs = new Dictionary<string, object>();
        request.PrivateArgs = new Dictionary<string, object>();

        request.Kwargs["id"] = request.Id;

        foreach (var key in form.Keys)
        {
            request.Kwargs[key] = form[key];
        }

        if (request.Task == AvailableModels.SpeechToText)
        {
            var file = form.Files["input"];
            if (file != null)
            {
               
                if (!AudioValidation.ValidateMediaType(file.ContentType))
                {
                    throw new InvalidOperationException("Invalid audio file type");
                }
                var fileName = $"/shared/temp/{Guid.NewGuid()}_{file.FileName}";
                using var stream = new FileStream(fileName, FileMode.Create);
                await file.CopyToAsync(stream);
                
                request.Kwargs["input"] = fileName;
            }
            //check if input is a url or base64
            if (request.Kwargs.ContainsKey("input") && request.Kwargs["input"] is string input)
            {
                if (!AudioValidation.ValidateMediaType(Path.GetExtension(input)))
                {
                    throw new InvalidOperationException("Invalid audio file type");
                }
                if (input.StartsWith("data:image"))
                {
                    var bytes = Convert.FromBase64String(input.Split(',')[1]);
                    var fileName = $"/shared/temp/{Guid.NewGuid()}{Path.GetExtension(input)}";
                    File.WriteAllBytes(fileName, bytes);
                    request.Kwargs["input"] = fileName;
                }

                if (input.StartsWith("http"))
                {
                    using var httpClient = new HttpClient();
                    var response = await httpClient.GetAsync(input);
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    if (!AudioValidation.ValidateMediaType(contentType))
                    {
                        throw new InvalidOperationException("Invalid audio file type from URL");
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    var extension = Path.GetExtension(input);
                    var fileName = $"/shared/temp/{Guid.NewGuid()}{extension}";
                    File.WriteAllBytes(fileName, bytes);
                    request.Kwargs["input"] = fileName;
                }
            }
        }

        return request;
    }

    public bool IsValidTask()
    {
        return AvailableModels.Names.ContainsKey(this.Task);
    }

    public (bool, string?) IsValidFields()
    {
        if (!Fields.ContainsKey(this.Task))
        {
            return (false, "Task not registered");
        }

        var config = Fields[this.Task];

        // Check mandatory fields first
        foreach (var field in config.MandatoryFields)
        {
            if (!this.Kwargs.ContainsKey(field))
            {
                return (false, $"Mandatory field '{field}' is missing");
            }
        }

        // Check if any of the alternative field groups are valid
        foreach (var fieldGroup in config.AlternativeFields)
        {
            bool isGroupValid = true;
            foreach (var field in fieldGroup)
            {
                if (!this.Kwargs.ContainsKey(field))
                {
                    isGroupValid = false;
                    break;
                }
            }
            if (isGroupValid)
            {
                return (true, null);
            }
        }

        // If no valid alternative field group is found, return error message
        var alternatives = string.Join(
            " or ",
            config.AlternativeFields.Select(group => $"'{string.Join(", ", group)}'")
        );
        return (false, $"Required alternative fields missing. Need {alternatives}");
    }
}
