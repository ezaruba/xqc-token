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
            while(true)
            {
                try
                {
                    // get upto 25 last incoming deposit transactions.
                    var incomingTransactions = await DepositAccount.GetIncomingTransactionsAsync();

                    // get up to 25 last outgoing payout transactions
                    var outgoingTransactions = await SecretAccount.GetOutgoingTransactionsAsync();

                    // get up to 25 unconfirmed outgoing payout transactions
                    var outgoingUncomfirmedTransactions = await SecretAccount.GetUnconfirmedTransactionsAsync();

                    // scan all incoming transactions to see if they need to be paid out assets
                    foreach (var t in incomingTransactions.data)
                    {
                       
                        // if the transaction isnt a transfer transaction or has zero amount, skip it.
                        // othertrans is support for multisig transfers
                        if (t.transaction.type != 257 && t.transaction?.otherTrans?.type != 257
                         || t.transaction?.amount <= 0 && t.transaction?.otherTrans?.amount <= 0) continue;
                        
                       
                        // if the deposit contains a mosaic, ignore - could be a refund.
                        if (t.transaction.mosaics != null
                         || t.transaction?.otherTrans?.mosaics != null) continue;

                        // if deposit account does an accidental payment to itself it will pay itself out, so ignore any transactions to self.
                        // manually convert tx signer to address in case public key of deposit address is not yet known.
                        if (new Address(AddressEncoding.ToEncoded(Con.GetNetworkVersion(), new PublicKey(t.transaction.type == 4100
                                ? t.transaction.otherTrans?.signer : t.transaction?.signer))).Encoded == DepositAccount.Address.Encoded) continue;
                
                        // assume all transacions unpaid
                        var transactionPaid = false;
            
                        // loop through all unconfirmed payout transactions
                        foreach (var tx in outgoingUncomfirmedTransactions.data)
                        {
                            // if the payout transaction message is null, its not needed to check, so skip.
                            if (tx.transaction?.message?.payload == null 
                                && tx.transaction?.otherTrans?.message?.payload == null) continue;

                            // if the message contains the hash of a deposit, its paid.
                            if (Encoding.UTF8.GetString(CryptoBytes.FromHexString(tx.transaction?.message?.payload ?? tx.transaction?.otherTrans?.message?.payload)) 
                                == (t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data))
                                transactionPaid = true;
                        }
                            
                        // as above, but checks unconfirmed to prevent paying out multiple times before the first payout is confirmed
                        foreach (var tx in outgoingTransactions.data)
                        {
                            // as above for confirmed transactions
                            if (tx.transaction?.message?.payload == null 
                                && tx.transaction?.otherTrans?.message?.payload == null) continue;

                            // as above for confirmed transactions
                            if (Encoding.UTF8.GetString(CryptoBytes.FromHexString(tx.transaction?.message?.payload ?? tx.transaction?.otherTrans?.message?.payload)) 
                                == (t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data))
                                transactionPaid = true;
                        }

                        // if the transaction t was paid, skip it.
                        if (transactionPaid) continue;
                      
                        // create the recipient      
                        var recipient = Con.GetNetworkVersion().ToEncoded(
                            new PublicKey(t.transaction.type == 4100      
                                ? t.transaction.otherTrans?.signer        
                                : t.transaction?.signer));

                        // calculate how quantity of asset to return.
                        var wholeAssetQuantity = (t.transaction.type == 4100 ? t.transaction.otherTrans.amount : t.transaction.amount)
                                                  / long.Parse(ConfigurationManager.AppSettings["xemPerMosaic"]);

                        // account for asset divisibility.
                        var assetUnits = (long)(wholeAssetQuantity * Math.Pow (10, double.Parse(MosaicToReturn.Properties[0].Value)));
                        
                        // payout XQC
                        await ReturnAsset(recipient, assetUnits, t.transaction.type == 4100 ? t.meta.innerHash.data : t.meta.hash.data);      
                    }                
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                Thread.Sleep(60000);
            }        
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
            }
            catch (Exception e)
            {
                // its normal to see some task cancled excpetions. its because a node might not respond in time.
                // if this happens it will pick up any unpaid deposits and pay them next time around so the exception can be ignored.
                Console.WriteLine(e);
            }
        }
    }
}
