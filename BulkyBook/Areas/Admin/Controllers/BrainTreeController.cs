using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Braintree;
using BulkyBook.Models;
using BulkyBook.Models.ViewModels;
using BulkyBook.Repository.IRepository;
using BulkyBook.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace BulkyBook.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class BrainTreeController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private TwilioSettings _twilioOptions { get; set; }
        //private readonly UserManager<IdentityUser> _userManager;
        [BindProperty]
        public ShoppingCartVM ShoppingCartVM { get; set; }
        
        public IBrainTreeGate _brain { get; set; }
        public BrainTreeController(IBrainTreeGate brain, IUnitOfWork unitOfWork, UserManager<IdentityUser> userManager, IOptions<TwilioSettings> twilionOptions)
        {
            _brain = brain;
            _unitOfWork = unitOfWork;
            _twilioOptions = twilionOptions.Value;
        //_userManager = userManager;
        }

        //public IActionResult Index()
        //{
        //    var gateway = _brain.GetGateway();
        //    var clientToken = gateway.ClientToken.Generate();
        //    ViewBag.ClientToken = clientToken;
        //    return View();
        //}

        public IActionResult Index()
        {
            var gateway = _brain.GetGateway();
            var clientToken = gateway.ClientToken.Generate();
            ViewBag.ClientToken = clientToken;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Index(IFormCollection collection)
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            ShoppingCartVM = new ShoppingCartVM()
            {
                OrderHeader = new Models.OrderHeader(),
                ListCart = _unitOfWork.ShoppingCart.GetAll(c => c.ApplicationUserId == claim.Value,
                                                            includeProperties: "Product")
            };

            ShoppingCartVM.OrderHeader.ApplicationUser = _unitOfWork.ApplicationUser
                                                            .GetFirstOrDefault(c => c.Id == claim.Value,
                                                                includeProperties: "Company");

            var count = 0;
            foreach (var list in ShoppingCartVM.ListCart)
            {
                list.Price = SD.GetPriceBasedOnQuantity(list.Count, list.Product.Price,
                                                    list.Product.Price50, list.Product.Price100);
                //ShoppingCartVM.OrderHeader.OrderTotal += (list.Price * list.Count);
                count++;
            }
            ShoppingCartVM.OrderHeader.Name = ShoppingCartVM.OrderHeader.ApplicationUser.Name;
            ShoppingCartVM.OrderHeader.PhoneNumber = ShoppingCartVM.OrderHeader.ApplicationUser.PhoneNumber;
            ShoppingCartVM.OrderHeader.StreetAddress = ShoppingCartVM.OrderHeader.ApplicationUser.StreetAddress;
            ShoppingCartVM.OrderHeader.City = ShoppingCartVM.OrderHeader.ApplicationUser.City;
            ShoppingCartVM.OrderHeader.State = ShoppingCartVM.OrderHeader.ApplicationUser.State;
            ShoppingCartVM.OrderHeader.PostalCode = ShoppingCartVM.OrderHeader.ApplicationUser.PostalCode;

            if (count != 0)
            {
                ShoppingCartVM.OrderHeader.PaymentStatus = SD.PaymentStatusPending;
                ShoppingCartVM.OrderHeader.OrderStatus = SD.StatusPending;
                ShoppingCartVM.OrderHeader.ApplicationUserId = claim.Value;
                ShoppingCartVM.OrderHeader.OrderDate = DateTime.Now;

                _unitOfWork.OrderHeader.Add(ShoppingCartVM.OrderHeader);
                _unitOfWork.Save();

                List<OrderDetails> orderDetailsList = new List<OrderDetails>();
                foreach (var item in ShoppingCartVM.ListCart)
                {
                    item.Price = SD.GetPriceBasedOnQuantity(item.Count, item.Product.Price,
                        item.Product.Price50, item.Product.Price100);
                    OrderDetails orderDetails = new OrderDetails()
                    {
                        ProductId = item.ProductId,
                        OrderId = ShoppingCartVM.OrderHeader.Id,
                        Price = item.Price,
                        Count = item.Count
                    };
                    ShoppingCartVM.OrderHeader.OrderTotal += orderDetails.Count * orderDetails.Price;
                    _unitOfWork.OrderDetails.Add(orderDetails);

                }

                _unitOfWork.ShoppingCart.RemoveRange(ShoppingCartVM.ListCart);
                _unitOfWork.Save();

                string nonceFromtheClient = collection["payment_method_nonce"];
                var request = new TransactionRequest
                {
                    Amount = (decimal)ShoppingCartVM.OrderHeader.OrderTotal,
                    PaymentMethodNonce = nonceFromtheClient,
                    OrderId = ShoppingCartVM.OrderHeader.Id.ToString(),
                    Options = new TransactionOptionsRequest
                    {
                        SubmitForSettlement = true
                    }
                };

                var gateway = _brain.GetGateway();
                Result<Transaction> result = gateway.Transaction.Sale(request);

                if (result.Target.ProcessorResponseText == "Approved")
                {
                    TempData["Success"] = "Transaction was successful Transaction ID "
                                    + ShoppingCartVM.OrderHeader.Id + ", Amount Charged : $" + (decimal)ShoppingCartVM.OrderHeader.OrderTotal;
                    HttpContext.Session.SetInt32(SD.ssShoppingCart, 0);
                    ShoppingCartVM.OrderHeader.TransactionId = result.Target.Id;
                    ShoppingCartVM.OrderHeader.PaymentStatus = SD.PaymentStatusApproved;
                    ShoppingCartVM.OrderHeader.OrderStatus = SD.StatusApproved;
                    ShoppingCartVM.OrderHeader.PaymentDate = DateTime.Now;
                    _unitOfWork.Save();
                    return RedirectToAction("OrderConfirmation", "BrainTree", new { id = ShoppingCartVM.OrderHeader.Id });
                }
                else
                {
                    ShoppingCartVM.OrderHeader.PaymentDueDate = DateTime.Now.AddDays(30);
                    ShoppingCartVM.OrderHeader.PaymentStatus = SD.PaymentStatusDelayedPayment;
                    ShoppingCartVM.OrderHeader.OrderStatus = SD.StatusApproved;
                    _unitOfWork.Save();
                }
            }
            
            return RedirectToAction("Index");
        }
        public IActionResult OrderConfirmation(int id)
        {
            OrderHeader orderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == id);
            TwilioClient.Init(_twilioOptions.AccountSid, _twilioOptions.AuthToken);
            try
            {
                var message = MessageResource.Create(
                    body: "Order Placed on Bulky Book. Your Order ID:" + id,
                    from: new Twilio.Types.PhoneNumber(_twilioOptions.PhoneNumber),
                    to: new Twilio.Types.PhoneNumber(orderHeader.PhoneNumber)
                    );
            }
            catch (Exception ex)
            {

            }



            return View(id);
        }

        //[HttpPost]
        //[ValidateAntiForgeryToken]
        //public IActionResult Index(IFormCollection collection)
        //{
        //    Random rnd = new Random();
        //    string nonceFromtheClient = collection["payment_method_nonce"];
        //    var request = new TransactionRequest
        //    {
        //        Amount = rnd.Next(1, 100),
        //        PaymentMethodNonce = nonceFromtheClient,
        //        OrderId = "55501",
        //        Options = new TransactionOptionsRequest
        //        {
        //            SubmitForSettlement = true
        //        }
        //    };

        //    var gateway = _brain.GetGateway();
        //    Result<Transaction> result = gateway.Transaction.Sale(request);

        //    if (result.Target.ProcessorResponseText == "Approved")
        //    {
        //        TempData["Success"] = "Transaction was successful Transaction ID "
        //                        + result.Target.Id + ", Amount Charged : $" + result.Target.Amount;
        //    }
        //    return RedirectToAction("Index");
        //}
    }
}