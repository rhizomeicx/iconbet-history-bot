﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IconSDK;
using IconSDK.RPCs;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Timers;
using System.Threading;
using System.Net.Http;
using System.Numerics;

namespace IconBetTransactionHistory
{
    class Program
    {
        private readonly static HttpClient client = new HttpClient();
        private static TelegramBotClient botClient;
        private const string TransactionFile = @"C:\iconbetbot\Data\Transactions.json";
        private const string PageCountFile = @"C:\iconbetbot\Data\PageCount.txt";
        private static System.Timers.Timer timer;
        private static long ChatId;

        static void Main(string[] args)
        {
            // GetAllTransactionList();
            botClient = new TelegramBotClient("942631975:AAFoMTG82fRD9cxjMFgReJ3HX4IiET5Ryos");

            //setting up bot to listen to conversation;
            botClient.OnMessage += Bot_Message;
            botClient.OnCallbackQuery += BotClient_OnCallbackQuery;
            botClient.StartReceiving(new UpdateType[] { UpdateType.CallbackQuery, UpdateType.Message });

            timer = new System.Timers.Timer(600000); //refresh transaction data every 10 minutes
            timer.Elapsed += Timer_Elapsed;

            timer.Start();

            while (true) { Thread.Sleep(7500); }
        }

        private static void Timer_Elapsed(object sender, ElapsedEventArgs el)
        {
            try
            {
                /*
                  the bot updates itself whenever someone requests last10 bets
                  however their could be a significant gap between each request
                  so each timer elapse get the most recent transactions on the chain
                  and combine
                */
                timer.Stop();
                Console.WriteLine("Retrieving most recent transactions on the Blockchain");

                var transactionList = new List<IconBetModel>();

                var currentPageCount = GetPageCount();
                Console.WriteLine($"Page Count on file: {currentPageCount}");
                var transactionDataOnFile = GetTransactionList();
                GetRecentTransactionList(currentPageCount, out int newPageCount, out transactionList);
                Console.WriteLine($"Current Page Count: {newPageCount}");

                var combinedTransactions = transactionDataOnFile.Concat(transactionList).ToList();
                InsertTransactionRecords(combinedTransactions, newPageCount);
            }
            catch { }
            finally
            {
                timer.Start();
            }
        }


        public static void LoadTransactions(string publicAddress, List<IconBetModel> transactionList, long chatId, int numberOfBets)
        {
            Console.WriteLine($"{DateTime.Now.ToLocalTime()}: Loading bet history for: {publicAddress} ");

            // Open the stream using a StreamReader for easy access.
            var count = 0;
            var transactionTotal = 0;
            var message = "";
            foreach (var tran in transactionList)
            {
                if (count == numberOfBets)
                {
                    // after we have found the first 10 bets jump out of the loop
                    break;
                }
                foreach (var d in tran.data)
                {
                    transactionTotal++;
                    if (d.fromAddr == publicAddress && count < numberOfBets)
                    {
                        count++;
                        var getTransactionByHash = new GetTransactionByHash(Consts.ApiUrl.MainNet);
                        var result = getTransactionByHash.Invoke(d.txHash).Result;

                        var json = JsonConvert.SerializeObject(result.Transaction.Data);
                        var betModel = JsonConvert.DeserializeObject<BetModel>(json.Replace("params", "param"));

                        var getTransactionResult = GetTransactionResult.Create(Consts.ApiUrl.MainNet);
                        var transactionResult = getTransactionResult(d.txHash).Result;

                        message += $"Bet Type: {betModel.method}\n";

                        if (betModel.method.Contains("number"))
                        {

                            message += $"Numbers Placed: {betModel.param.numbers} \n";
                        }


                        if (betModel.method.Contains("color"))
                        {

                            var color = string.Empty;
                            switch (betModel.param.color)
                            {
                                case 0:
                                    color = "Black";
                                    break;
                                case 1:
                                    color = "Red";
                                    break;
                            }

                            message += $"Bet Option Placed: {color}\n";
                        }

                        if (betModel.method.Contains("even_odd"))
                        {
                            var even_odd = string.Empty;

                            switch (betModel.param.even_odd)
                            {
                                case 0:
                                    even_odd = "Even";
                                    break;
                                case 1:
                                    even_odd = "Odd";
                                    break;
                            }

                            message += $"Bet Option Placed: {even_odd}\n";
                        }
                        bool win = false;
                        foreach (var log in transactionResult.EventLogs)
                        {
                            var eventTransactionString = JsonConvert.SerializeObject(log);
                            var transactionResultModel = JsonConvert.DeserializeObject<TransactionResultModel>(eventTransactionString);

                            if (transactionResultModel.Indexed[0].Contains("BetPlaced"))
                            {
                                var betAmont = transactionResultModel.Indexed[1];
                                if (betAmont.StartsWith("0x"))
                                {
                                    betAmont = betAmont.Substring(2, betAmont.Length - 2);
                                }

                                BigInteger number = BigInteger.Parse(betAmont, System.Globalization.NumberStyles.AllowHexSpecifier);


                                var amountBet = NumericHelper.Loop2ICX(number.ToString());

                                message += $"Amount placed {amountBet} ICX \n";
                            }


                            if (transactionResultModel.Indexed[0].Contains("BetResult"))
                            {
                                message += $"Wheel stopped on number: {transactionResultModel.Indexed[2]}\n";
                            }

                            if (transactionResultModel.Indexed[0].Contains("ICXTransfer"))
                            {
                                var betWinings = transactionResultModel.Indexed[3];

                                if (betWinings.StartsWith("0x"))
                                {
                                    betWinings = betWinings.Substring(2, betWinings.Length - 2);
                                }

                                BigInteger number = BigInteger.Parse(betWinings, System.Globalization.NumberStyles.AllowHexSpecifier);
                                var amountWon = NumericHelper.Loop2ICX(number.ToString());
                                message += $"You won {amountWon} ICX \n";
                            }

                            foreach (var eventData in log.Data)
                            {
                                if (eventData.ToUpper().Contains("WINNINGS"))
                                {
                                    win = true;
                                    break;
                                }
                            }
                        }

                        if (win)
                        {
                            message += "Bet Result: WIN!\n";
                        }
                        else
                        {
                            message += "Bet Result: LOST!\n";
                        }

                        message += "-----------------------------------------\n";
                    }
                }

            }
            SendTelegram(message, chatId);
        }

        public static void GetRecentTransactionList(int pageCount, out int newPageCount, out List<IconBetModel> transactionListOut)
        {
            bool data = true;
            var transactionList = new List<IconBetModel>();

            while (data)
            {
                var result = client.GetAsync("https://tracker.icon.foundation/v3/contract/txList?page=" + pageCount + "&count=10000&addr=cx1b97c1abfd001d5cd0b5a3f93f22cccfea77e34e").Result;
                var responseBody = result.Content.ReadAsStringAsync().Result;

                var iconBetTransactions = JsonConvert.DeserializeObject<IconBetModel>(responseBody);

                if (iconBetTransactions.description != "success")
                {
                    data = false;
                }
                else
                {
                    transactionList.Add(iconBetTransactions);
                    pageCount++;
                }

            }
            transactionListOut = transactionList;
            newPageCount = pageCount;
        }

        public static void InsertTransactionRecords(List<IconBetModel> transactionList, int pageCount)
        {
            var fileContents = JsonConvert.SerializeObject(transactionList);

            File.WriteAllText(TransactionFile, fileContents);
            File.WriteAllText(PageCountFile, pageCount.ToString());
        }

        public static int GetPageCount()
        {

            try
            {
                int pageCount = Convert.ToInt16(System.IO.File.ReadAllText(PageCountFile));
                return pageCount;
            }
            catch
            {
                return 1;
            }
        }

        public static List<IconBetModel> GetTransactionList()
        {
            try
            {
                var transactionDataFile = File.ReadAllText(TransactionFile);
                return JsonConvert.DeserializeObject<List<IconBetModel>>(transactionDataFile);
            }
            catch
            {
                return new List<IconBetModel>();
            }
        }

        private static void Bot_Message(object sender, Telegram.Bot.Args.MessageEventArgs e)
        {
            try
            {
                if (e.Message.Type == MessageType.Text)
                {
                    ChatId = e.Message.Chat.Id;
                    var message = e.Message.Text;
                    if (message.Length > 0 && message[0] == '/')
                    {
                        ExecuteCommand(message, e.Message.Chat.Id);
                    }
                }
            }
            catch { }
        }

        private static void BotClient_OnCallbackQuery(object sender, CallbackQueryEventArgs e)
        {
            try
            {
                if (e.CallbackQuery.Message.Chat.Id == ChatId)
                {
                    ExecuteCommand(e.CallbackQuery.Data, e.CallbackQuery.Message.Chat.Id);
                }
            }
            finally { }
        }

        private static void ExecuteCommand(string message, long chatId)
        {
            string args = "";
            var command = message.Replace("/", string.Empty).Split(' ');
            if (command.Length > 1)
            {
                args = command[1];
            }
            if (command[0].StartsWith("last10bets")) ShowLast10(args, chatId);
            if (command[0].StartsWith("start")) ShowMenu(chatId);
            //if (command[0].StartsWith("orders")) GetOpenOrders(_config);
        }


        public static void SendTelegram(string message, long chatId, ReplyKeyboardMarkup keyboardMarkup = null)
        {
            try
            {
                var result = botClient.SendTextMessageAsync(new Telegram.Bot.Types.ChatId(chatId), message, ParseMode.Markdown, replyMarkup: keyboardMarkup).Result;
            }
            catch { }
        }

        private static void ShowMenu(long chatId)
        {
            var message = "Available commands are: \n";

            message += "/last10bets <public address>\n";
            message += "for example \n";
            message += "/last10bets hxc9e36a98a3fca0b636eb822ff5a96db658e4bb88";

            SendTelegram(message, chatId);


            //  string message = "*Select an option*";
            //   var keyboard = new ReplyKeyboardMarkup()
            //  {
            //     Keyboard = new[] {new KeyboardButton[]
            //            {
            //                        "/orders",
            //              "/last10bets",
            //               "/start"
            //        }
            //     };
            //   keyboard.ResizeKeyboard = true;
            //   SendTelegram(message, chatId, keyboard);
        }

        public static void ShowLast10(string publicAddress, long chatId)
        {
            SendTelegram("Fetching results...please wait..", chatId);

            try
            {
                var transactionList = new List<IconBetModel>();

                var currentPageCount = GetPageCount();
                var transactionDataOnFile = GetTransactionList();
                GetRecentTransactionList(currentPageCount, out int newPageCount, out transactionList);

                var combinedTransactions = transactionDataOnFile.Concat(transactionList).ToList();

                LoadTransactions(publicAddress, combinedTransactions, chatId, 10);

                //update the Data file containing transactions
                InsertTransactionRecords(combinedTransactions, newPageCount);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }
    }
}