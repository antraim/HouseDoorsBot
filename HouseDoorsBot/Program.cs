using Refit;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

const string HOUSE_API_URL = "HOUSE_API_URL";
const string HOUSE_AUTH_TOKEN = "HOUSE_AUTH_TOKEN";
const string HOUSE_BOT_ACCESS_TOKEN = "HOUSE_BOT_ACCESS_TOKEN";

var HouseApiUrl = Environment.GetEnvironmentVariable(HOUSE_API_URL)
	.ThrowIfNullOrWhiteSpace(HOUSE_API_URL, "Environment Variable is null or empty.");
var HouseAuthToken = Environment.GetEnvironmentVariable(HOUSE_AUTH_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_AUTH_TOKEN, "Environment Variable is null or empty.");
var HouseBotAccessToken = Environment.GetEnvironmentVariable(HOUSE_BOT_ACCESS_TOKEN)
	.ThrowIfNullOrWhiteSpace(HOUSE_BOT_ACCESS_TOKEN, "Environment Variable is null or empty.");

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

	Console.WriteLine($"Received a '{messageText}' message in chat {chatId}.");

	var text = string.Empty;

	if (messageText.Equals("/start"))
		text = "Привет, я открываю двери в хату, йоу!";
	else
	{
		text = messageText switch
		{
			"0" => await SendAsync(HouseAuthToken, Doors.Entrance),
			"1" => await SendAsync(HouseAuthToken, Doors.Main),
			"2" => await SendAsync(HouseAuthToken, Doors.NearShop),
			"3" => await SendAsync(HouseAuthToken, Doors.NearParking),
			_ => "Таких команд я не знаю:(",
		};
	}

	Console.WriteLine($"Answer: '{text}'. message in chat {chatId}.");

	var sentMessage = await botClient.SendTextMessageAsync(
		chatId: chatId,
		text,
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

async Task<string> SendAsync(string token, Doors door)
{
	var api = RestService.For<IApi>(HouseApiUrl);
	var requestId = $"3ED{Random.Shared.Next(10, 99)}FC0-6E{Random.Shared.Next(10, 99)}-{Random.Shared.Next(10, 99)}C8-AD5A-8AD0A{Random.Shared.Next(10, 99)}A1F{Random.Shared.Next(10, 99)}";

	try
	{
		var result = await api.SendAsync($"Bearer {token}", requestId, (int)door);

		return result.StatusCode.ToString();
	}
	catch (ApiException apiException)
	{
		return apiException.Message;
	}
}

enum Doors
{
	Entrance = 6700,
	Main = 6701,
	NearShop = 13519,
	NearParking = 14149
}

interface IApi
{
	[Post("/app/devices/{doorId}/open")]
	Task<IApiResponse> SendAsync(
		[Header("Authorization")] string token,
		[Header("X-Request-Id")] string requestId,
		int doorId);
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