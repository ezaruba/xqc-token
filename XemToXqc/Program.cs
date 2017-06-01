using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSharp2nem;
using System.Configuration;
using System.Threading;
using Chaos.NaCl;


namespace XemToXqc
{
    class Program
    {
        private static Connection Con { get; set; }
        private static UnverifiableAccount DepositAccount { get; set; }
        private static VerifiableAccount SecretAccount { get; set; }
        private static MosaicDefinitions.Mosaic MosaicToReturn { get; set; }
        private static Task T { get; set; }
        static void Main(string[] args)
        {
            // create connection
            Con = new Connection();

            // set to testnet, remove for live net.
            Con.SetTestNet();

            // create deposit account
            DepositAccount = new AccountFactory(Con).FromEncodedAddress(ConfigurationManager.AppSettings["depositAccountAddress"]);

            // create mosaic issuer/distributor account
            SecretAccount = new AccountFactory(Con).FromPrivateKey(ConfigurationManager.AppSettings["secretAccountKey"]);

            // get required mosaic details
            MosaicToReturn = SecretAccount.GetMosaicsAsync().Result.Data.Single(
                e => e.Id.Name == ConfigurationManager.AppSettings["mosaicNameID"] &&
                e.Id.NamespaceId == ConfigurationManager.AppSettings["mosaicNameSpace"]);

            // run task to scan for incoming transactions and return x amount of asset
            T = Task.Run(() => ScanTransactions());

            Console.WriteLine("Bot started..");

            Console.ReadKey();
        }

        private static async void ScanTransactions()
        {
            string lastIncomingTxHash = null;

            var transactionsOut = GetAllPaidTransactions();
           
            while (true)
            {
                try
                {
                    // get upto 25 last incoming deposit transactions up to the hash supplied.
                    var incomingTransactions = await DepositAccount.GetIncomingTransactionsAsync(lastIncomingTxHash);

                    // if no transactions, break.
                    if (incomingTransactions?.data?.Count == 0 || incomingTransactions?.data == null)
                    {
                        break;
                    }
                   
                    // scan all incoming transactions to see if they need to be paid out assets
                    foreach (var t in incomingTransactions.data)
                    {
                        // set last hash to the hash of the last transaction in the previous list. (null on the first go round)
                        // you can only retrieve 25 txs at a time. providing the hash of the last transaction 
                        // in the previous list will give the next 25 transactions that happened prior to the hash provided
                        lastIncomingTxHash = t.meta.hash.data;

                        // assume unpaid
                        var paid = false;

                        // loop all outgoing and unconfirmed outgoing transactions
                        foreach (var confTx in transactionsOut.Result)
                        {
                            // if message is not null, could contain a payout hash
                            if (confTx?.transaction?.message?.payload == null) continue;

                            // if the hash in the outgoing message matches the hash of the deposit transaction being checked,
                            // its been paid, so print out, declare paid and stop looping through outgoing txs
                            if (Encoding.UTF8.GetString(CryptoBytes.FromHexString(confTx.transaction.message.payload)) 
                                != (t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data)) continue;

                            // print out paid hash    
                            Console.WriteLine("hash of paid tx:");
                            Console.WriteLine(Encoding.UTF8.GetString(CryptoBytes.FromHexString(confTx.transaction.message.payload)));
                            Console.WriteLine();

                            paid = true;
                            break;
                        }

                        // if paid, continue to check the next incoming transaction
                        if (paid) continue;

                        Console.WriteLine(t.transaction?.amount);
                        // if the transaction isnt a transfer transaction, skip it.
                        // othertrans is support for multisig transfers. 
                        if (t.transaction.type != 257 && t.transaction?.otherTrans?.type != 257) continue;


                        // if deposit account does an accidental payment to itself it will pay itself out, so ignore any transactions to self on the off chance it happens.
                        //    manually convert tx signer to address in case public key of deposit address is not yet known.
                        if (new Address(Con.GetNetworkVersion().ToEncoded(new PublicKey(t.transaction.type == 4100
                                ? t.transaction.otherTrans?.signer : t.transaction?.signer))).Encoded == DepositAccount.Address.Encoded) continue;                
                      
                        // create the recipient      
                        var recipient = Con.GetNetworkVersion().ToEncoded(
                            new PublicKey(t.transaction.type == 4100      
                                ? t.transaction.otherTrans?.signer        
                                : t.transaction?.signer));


                        // declare 
                        double wholeAssetQuantity = 0.0;

                        // get cost of asset
                        var xemPerMosaic = double.Parse(ConfigurationManager.AppSettings["xemPerMosaic"]);

                        // calculate quantity of asset to return.
                        wholeAssetQuantity += (t.transaction.type == 4100
                                                    ? t.transaction.otherTrans.amount
                                                    : t.transaction.amount) 
                                                / xemPerMosaic;

                        // if transaction contains a mosaic of type xem, calculate whole assets to be paid out and include.
                        // xem can be sent both as version one and two type transactions ie. attached as a mosaic. in some cases people may send xem as a mosaic
                        // of type xem so catch it if they do.
                        if ((t.transaction.type == 4100 ? t.transaction.otherTrans.mosaics : t.transaction.mosaics) != null)
                        {
                            var mosaic = (t.transaction.type == 4100 ? t.transaction.otherTrans.mosaics : t.transaction.mosaics)
                                             .Single(e =>  e.mosaicId.namespaceId == "nem" && e.mosaicId.name == "xem");

                            wholeAssetQuantity += mosaic.quantity / xemPerMosaic;
                        }
 
                        // account for asset divisibility.
                        var assetUnits = (long)(wholeAssetQuantity * Math.Pow (10, long.Parse(MosaicToReturn.Properties[0].Value)));

                        // print out incoming hash
                        Console.WriteLine("incoming hash to pay: \n" + (t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data)); 

                        // payout asset
                        await ReturnAsset(recipient, assetUnits, t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data);

                        // could flood the network with transactions, so limit to 1 transactions per min, 
                        // also helps disperse transaction fees among harvesters.
                        Thread.Sleep(60000);
                    }       
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            Console.WriteLine("all transactions checked and paid");
        }

        private static async Task<List<Transactions.TransactionData>> GetAllPaidTransactions()
        {
            // set to null so that it retrieves the newest set of transactions
            string lastHashConfirmed = null;
            
            // declare transactions list
            var transactions = new List<Transactions.TransactionData>();
            
            // loop through all outgoing transactions and aggregate them until none are left.
            while (true)
            {
                // get all transactions up to the last hash checked in previous loop
                var t = await SecretAccount.GetOutgoingTransactionsAsync(lastHashConfirmed);

                // break if theres no transactions
                if (t?.data == null || t?.data?.Count == 0)
                {
                    break;
                }

                // if there are more transactions, set the last hash to that of the last hash in the list of transactions
                lastHashConfirmed = t.data[t.data.Count - 1].meta.hash.data;

                // add any transactions retrieved to the list.
                transactions.AddRange(t.data);
            }

            // get up to 25 unconfirmed outgoing payout transactions to prevent paying out twice before txs confirm.
            var outgoingUncomfirmedTransactions = await SecretAccount.GetUnconfirmedTransactionsAsync();

            // add them to the list
            transactions.AddRange(outgoingUncomfirmedTransactions.data);

            Console.WriteLine();

            return transactions;
        }

        private static async Task ReturnAsset(string address, long amount, string hash)
        {
            try
            {               
                // create mosaic to be sent
                var mosaicsToSend = new List<Mosaic>()
                {              
                    new Mosaic(MosaicToReturn.Id.NamespaceId,
                               MosaicToReturn.Id.Name,
                               amount)
                };

                Console.WriteLine("address to send to: \n" + address);

                // initialize transaction data
                var transferData = new TransferTransactionData()
                {
                    Amount = 0, // no xem to be sent but is still required.
                    Message = hash, // include the hash of the deposit transaction for tracability
                    ListOfMosaics = mosaicsToSend, // include list of mosaics to send, in this case just one but needs to be a list.
                    Recipient = new AccountFactory(Con).FromEncodedAddress(address) // recipient of the transaction
                };

                // send the transaction
                var result = await SecretAccount.SendTransactionAsync(transferData);

                // print out result
                Console.WriteLine(result.Message);
                Console.WriteLine();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
