using OpenAI_API;
using OpenAI_API.Completions;
using OpenAI_API.Models;

using Refit;

using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

const string HOUSE_API_URL = "HOUSE_API_URL";
const string HOUSE_AUTH_TOKEN = "HOUSE_AUTH_TOKEN";
const string HOUSE_BOT_ACCESS_TOKEN = "HOUSE_BOT_ACCESS_TOKEN";
const string HOUSE_BOT_USERS = "HOUSE_BOT_USERS";
const string ENVIRONMENT_VARIABLE_ERROR_MESSAGE = "Environment Variable is null or empty.";

var HouseApiUrl = Environment.GetEnvironmentVariable(HOUSE_API_URL)
	.ThrowIfNullOrWhiteSpace(HOUSE_API_URL, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseAuthToken = Environment.GetEnvironmentVariable(HOUSE_AUTH_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_AUTH_TOKEN, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseBotAccessToken = Environment.GetEnvironmentVariable(HOUSE_BOT_ACCESS_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_BOT_ACCESS_TOKEN, ENVIRONMENT_VARIABLE_ERROR_MESSAGE);
var HouseBotUsers = Environment.GetEnvironmentVariable(HOUSE_BOT_USERS)?.Split(',');

var CommandsDictionary = new ReadOnlyDictionary<string, Commands>(new Dictionary<string, Commands>
{
	{ "0", Commands.OpenEntranceDoor },
	{ "1", Commands.OpenMainDoor },
	{ "2", Commands.OpenNearShopDoor },
	{ "3", Commands.OpenNearParkingDoor},
	{ "111", Commands.GenerateCode},
	{ "000", Commands.DeleteCode},
});

var CommandDoorDictionary = new ReadOnlyDictionary<Commands, Doors>(new Dictionary<Commands, Doors>
{
	{ Commands.OpenEntranceDoor, Doors.Entrance },
	{ Commands.OpenMainDoor, Doors.Main },
	{ Commands.OpenNearShopDoor, Doors.NearShop },
	{ Commands.OpenNearParkingDoor, Doors.NearParking },
});

var botClient = new TelegramBotClient(HouseBotAccessToken);

ReceiverOptions receiverOptions = new()
{
	AllowedUpdates = Array.Empty<UpdateType>()
};

using CancellationTokenSource cts = new();

botClient.StartReceiving(
	updateHandler: HandleUpdateAsync,
	pollingErrorHandler: HandlePollingErrorAsync,
	receiverOptions: receiverOptions,
	cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();

Console.WriteLine($"Start listening for @{me.Username}");
Console.ReadLine();

cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
	if (update.Message is not { } message)
		return;
	if (message.Text is not { } messageText)
		return;

	var chatId = message.Chat.Id;
	var user = $"{message.Chat.FirstName} {message.Chat.LastName} (@{message.Chat.Username})";

	Console.WriteLine($"Received a '{messageText}' message from {user}, chat {chatId}.");

	var result = await ExecuteCommandAsync(chatId, messageText);

	Console.WriteLine($"Answer: '{result}'. Message to {user}, chat {chatId}.");

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: chatId,
		result,
		replyToMessageId: update.Message.MessageId,
		allowSendingWithoutReply: true,
		cancellationToken: cancellationToken);
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
	var ErrorMessage = exception switch
	{
		ApiRequestException apiRequestException
			=> $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
		_ => exception.ToString()
	};

	Console.WriteLine(ErrorMessage);

	return Task.CompletedTask;
}

async Task<string> ExecuteCommandAsync(long chatId, string messageText)
{
	const int FlatId = 451352;

	if (messageText.Equals("/start"))
		return "Hello:)";

	if (HouseBotUsers is not null)
		if (!HouseBotUsers.Contains(chatId.ToString()))
			return "No access";

	if (messageText.Equals("/help"))
		return GetAvailableCommands();

	var isExistCommand = CommandsDictionary.TryGetValue(messageText, out var command);

	if (isExistCommand)
	{
		var isOpenDoorCommand = CommandDoorDictionary.TryGetValue(command, out var door);

		if (isOpenDoorCommand)
			return await OpenDoorCommandAsync(door);

		if (command is Commands.GenerateCode)
			return await GenerateCodeCommandAsync(FlatId);

		if (command is Commands.DeleteCode)
			return await DeleteCodeCommandAsync(FlatId);

		return "There is no such command";
	}
	else
		return await GetResponseFromChatGPTAsync(messageText);
}

async Task<string> OpenDoorCommandAsync(Doors door)
{
	var api = RestService.For<IApi>(HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.OpenDoorAsync(HouseAuthToken, requestId, (short)door)
			.HandleExceptionAndGetStringResult(it => it.IsSuccessStatusCode
				? $"Door ({door}) is opened"
				: $"Error [{it.StatusCode.ToString()}]");
}

async Task<string> GenerateCodeCommandAsync(int flatId)
{
	var api = RestService.For<IApi>(HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.GenerateCodeAsync(HouseAuthToken, requestId,
		new
		{
			devices_ids = Array.ConvertAll((Doors[])Enum.GetValues(typeof(Doors)), value => (int)value),
			flat_id = flatId
		})
		.HandleExceptionAndGetStringResult(it =>
		{
			if (!it.IsSuccessStatusCode)
				return $"Error [{it.StatusCode.ToString()}]";

			using var jsonDocument = JsonDocument.Parse(it.Content);

			var isExistData = jsonDocument.RootElement.TryGetProperty("data", out var dataElement);
			var isExistCode = dataElement.TryGetProperty("code", out var codeElement);
			var isExistExpiresAt = dataElement.TryGetProperty("expires_at", out var expiresAtElement);
			var sb = new StringBuilder();

			sb.AppendLine($"Code: {codeElement.GetString()}");
			sb.AppendLine($"Expires at: {DateTime.Parse(expiresAtElement.GetString()).ToString("g")}");

			return sb.ToString();
		});
}

async Task<string> DeleteCodeCommandAsync(int flatId)
{
	var api = RestService.For<IApi>(HouseApiUrl);
	var requestId = GenerateRequestId();

	return await api.DeleteCodeAsync(HouseAuthToken, requestId, flatId)
			.HandleExceptionAndGetStringResult(it => it.StatusCode is HttpStatusCode.NoContent
				? $"Last code is deleted"
				: $"Error [{it.StatusCode.ToString()}]");
}

string GenerateRequestId() => Guid.NewGuid().ToString().ToUpperInvariant();

async Task<string> GetResponseFromChatGPTAsync(string messageText)
{
	try
	{
		var api = new OpenAIAPI(APIAuthentication.LoadFromEnv());

		return await api.Completions.CreateAndFormatCompletion(
			new CompletionRequest(
				messageText,
				Model.DavinciText,
				max_tokens: 1000,
				temperature: 0.9,
				top_p: 1,
				presencePenalty: 0.6,
				frequencyPenalty: 0));
	}
	catch (Exception exeption)
	{
		return $"Error generating a response from ChatGPT ({exeption.Message})";
	}
}

string GetAvailableCommands()
{
	var sb = new StringBuilder();

	sb.AppendLine("Available Commands (just write a number):");

	foreach (var command in CommandsDictionary)
		sb.AppendLine($"{command.Key} - {command.Value}");

	sb.AppendLine("Or write something to chat with ChatGPT");

	return sb.ToString();
}

enum Commands : byte
{
	OpenEntranceDoor,
	OpenMainDoor,
	OpenNearShopDoor,
	OpenNearParkingDoor,
	GenerateCode,
	DeleteCode
}

enum Doors : short
{
	Entrance = 6700,
	Main = 6701,
	NearShop = 13519,
	NearParking = 14149
}

[Headers("User-Agent: :)")]
interface IApi
{
	[Post("/v2/app/devices/{doorId}/open")]
	Task<IApiResponse<string>> OpenDoorAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		short doorId);

	[Post("/v3/app/codes/generate")]
	Task<IApiResponse<string>> GenerateCodeAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		[Body] object body);

	[Delete("/v2/app/flats/{flatId}/intercode")]
	Task<IApiResponse<string>> DeleteCodeAsync(
		[Authorize("Bearer")] string token,
		[Header("X-Request-Id")] string requestId,
		int flatId);
}

static class TaskExtention
{
	public static async Task<string> HandleExceptionAndGetStringResult(this Task<IApiResponse<string>> task, Func<IApiResponse<string>, string> func)
	{
		try
		{
			var result = await task;

			return func(result);
		}
		catch (ApiException apiException)
		{
			return $"Exception ({apiException.Message})";
		}
	}
}

static class ArgumentExceptionExtention
{
	public static string ThrowIfNullOrWhiteSpace(this string argument, string argumentName, string message)
	{
		if (string.IsNullOrWhiteSpace(argument))
			ThrowArgumentNullException(argumentName, message);

		return argument;
	}

	private static void ThrowArgumentNullException(string argumentName, string message)
		=> throw new ArgumentNullException(argumentName, message);
}