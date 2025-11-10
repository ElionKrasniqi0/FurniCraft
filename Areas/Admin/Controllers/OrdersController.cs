using FurniCraft.Data;
using FurniCraft.Enum;
using FurniCraft.Models;
using FurniCraft.ViewModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Net;
using System.Net.Mail;
using System.Text; // Add this using statement

namespace FurniCraft.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class OrdersController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<IdentityUser> _userManager;


        public OrdersController(ApplicationDbContext context, UserManager<IdentityUser> userManager)
        {
            _context = context;
            _userManager = userManager;

        }

        public async Task<IActionResult> Index(string status = "", string search = "")
        {
            var query = _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails)
                .ThenInclude(od => od.Product)
                .AsQueryable();

            // Filter by status - FIXED: Use System.Enum explicitly
            if (!string.IsNullOrEmpty(status))
            {
                if (System.Enum.TryParse<OrderStatus>(status, out var orderStatus))
                {
                    query = query.Where(o => o.Status == orderStatus);
                }
            }

            // Search functionality
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(o =>
                    o.OrderId.ToString().Contains(search) ||
                    o.User.UserName.Contains(search) ||
                    o.User.Email.Contains(search) ||
                    o.ShippingAddress.Contains(search) ||
                    o.PhoneNumber.Contains(search));
            }

            var orders = await query
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.SearchQuery = search;

            // FIXED: Use System.Enum explicitly
            ViewBag.StatusList = System.Enum.GetValues(typeof(OrderStatus)).Cast<OrderStatus>();

            return View(orders);
        }

        public async Task<IActionResult> Details(int id)
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails)
                .ThenInclude(od => od.Product)
                .ThenInclude(p => p.Category)
                .FirstOrDefaultAsync(o => o.OrderId == id);

            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }
        [HttpPost]
        public async Task<IActionResult> AddTrackingEvent(int orderId, string location, string status, string description, bool isMilestone = false)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null)
            {
                return Json(new { success = false, message = "Order not found." });
            }

            var trackingEvent = new TrackingEvent
            {
                OrderId = orderId,
                EventDate = DateTime.Now,
                Location = location,
                Status = status,
                Description = description,
                IsMilestone = isMilestone
            };

            _context.TrackingEvents.Add(trackingEvent);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Tracking event added successfully." });
        }

        [HttpGet]
        public async Task<IActionResult> GetTrackingHistory(int orderId)
        {
            var trackingEvents = await _context.TrackingEvents
                .Where(te => te.OrderId == orderId)
                .OrderByDescending(te => te.EventDate)
                .ToListAsync();

            return Json(trackingEvents);
        }

       
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, OrderStatus status, string trackingNumber = "", string adminNotes = "")
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails)
                .ThenInclude(od => od.Product)
                .FirstOrDefaultAsync(o => o.OrderId == id);

            if (order == null)
            {
                return Json(new { success = false, message = "Order not found." });
            }

            var oldStatus = order.Status;
            order.Status = status;

            // Handle null values
            order.TrackingNumber = trackingNumber ?? string.Empty;
            order.AdminNotes = adminNotes ?? string.Empty;

            // Update status dates
            var now = DateTime.Now;
            switch (status)
            {
                case OrderStatus.Verified:
                    order.VerifiedDate = now;
                    break;
                case OrderStatus.Processing:
                    order.ProcessingDate = now;
                    break;
                case OrderStatus.Shipped:
                    order.ShippedDate = now;
                    break;
                case OrderStatus.Completed:
                    order.CompletedDate = now;
                    break;
                case OrderStatus.Cancelled:
                    order.CancelledDate = now;
                    break;
            }

            await _context.SaveChangesAsync();

            // Add automated tracking event
            await AddAutomatedTrackingEvent(order.OrderId, oldStatus, status);

            // Send email notification to customer
            try
            {
                await SendOrderStatusEmail(order, oldStatus, status);
                return Json(new
                {
                    success = true,
                    message = "Order status updated successfully and email notification sent."
                });
            }
            catch (Exception ex)
            {
                // Log the error but don't prevent the status update
                Console.WriteLine($"Failed to send status email: {ex.Message}");
                return Json(new
                {
                    success = true,
                    message = "Order status updated but failed to send email notification."
                });
            }
        }

        private async Task AddAutomatedTrackingEvent(int orderId, OrderStatus oldStatus, OrderStatus newStatus)
        {
            var trackingMessage = newStatus switch
            {
                OrderStatus.Received => "Order received and being processed",
                OrderStatus.Verified => "Order verified and payment confirmed",
                OrderStatus.Processing => "Order is being prepared for shipment",
                OrderStatus.Shipped => "Order has been shipped",
                OrderStatus.Completed => "Order delivered successfully",
                OrderStatus.Cancelled => "Order has been cancelled",
                _ => "Order status updated"
            };

            var trackingEvent = new TrackingEvent
            {
                OrderId = orderId,
                EventDate = DateTime.Now,
                Location = "Warehouse",
                Status = newStatus.ToString(),
                Description = trackingMessage,
                IsMilestone = true
            };

            _context.TrackingEvents.Add(trackingEvent);
            await _context.SaveChangesAsync();
        }

        private async Task SendOrderStatusEmail(Order order, OrderStatus oldStatus, OrderStatus newStatus)
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .Build();

                var emailSettings = configuration.GetSection("EmailSettings");

                var fromEmail = emailSettings["SenderEmail"];
                var fromPassword = emailSettings["SenderPassword"];
                var smtpHost = emailSettings["SmtpServer"];
                var smtpPort = emailSettings["SmtpPort"];
                var senderName = emailSettings["SenderName"];

                if (string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(fromPassword))
                {
                    throw new Exception("Email configuration is missing. Please check appsettings.json.");
                }

                if (!int.TryParse(smtpPort, out int port))
                {
                    port = 587; // Default SMTP port
                }

                // Build email subject and body
                var subject = $"Order Status Update - Order #ORD{order.OrderId:D6}";
                var emailBody = BuildOrderStatusEmailBody(order, oldStatus, newStatus);

                var message = new MailMessage
                {
                    From = new MailAddress(fromEmail, senderName),
                    Subject = subject,
                    Body = emailBody,
                    IsBodyHtml = true
                };

                message.To.Add(order.User.Email);

                // Add CC to admin for monitoring (optional)
                // message.CC.Add("admin@furnicraft.com");

                using (var smtpClient = new SmtpClient(smtpHost, port))
                {
                    smtpClient.Credentials = new NetworkCredential(fromEmail, fromPassword);
                    smtpClient.EnableSsl = true;
                    smtpClient.Timeout = 30000;

                    await smtpClient.SendMailAsync(message);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to send order status email: {ex.Message}");
            }
        }

        private string BuildOrderStatusEmailBody(Order order, OrderStatus oldStatus, OrderStatus newStatus)
        {
            var sb = new StringBuilder();

            sb.AppendLine($@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: #3b5d50; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; }}
        .status-update {{ background: white; padding: 15px; margin: 15px 0; border-radius: 5px; border-left: 4px solid #3b5d50; }}
        .order-details {{ background: white; padding: 15px; margin: 15px 0; border-radius: 5px; }}
        .tracking-info {{ background: #e8f5e8; padding: 15px; margin: 15px 0; border-radius: 5px; border-left: 4px solid #28a745; }}
        .footer {{ text-align: center; margin-top: 20px; padding: 20px; background: #f1f1f1; border-radius: 0 0 5px 5px; }}
        .status-badge {{ display: inline-block; padding: 5px 10px; border-radius: 15px; font-weight: bold; margin: 0 5px; }}
        .status-new {{ background: #007bff; color: white; }}
        .status-processing {{ background: #ffc107; color: black; }}
        .status-shipped {{ background: #17a2b8; color: white; }}
        .status-completed {{ background: #28a745; color: white; }}
        .status-cancelled {{ background: #dc3545; color: white; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>FurniCraft</h1>
            <h2>Order Status Update</h2>
        </div>
        
        <div class='content'>
            <p>Dear {order.User?.UserName ?? "Valued Customer"},</p>
            <p>We're writing to inform you about an update to your order status.</p>
            
            <div class='status-update'>
                <h3>Status Change</h3>
                <p>
                    Your order status has been updated from 
                    <span class='status-badge status-{oldStatus.ToString().ToLower()}'>@{oldStatus}</span> 
                    to 
                    <span class='status-badge status-{newStatus.ToString().ToLower()}'>@{newStatus}</span>
                </p>
            </div>

            <div class='order-details'>
                <h3>Order Information</h3>
                <p><strong>Order Number:</strong> #ORD{order.OrderId:D6}</p>
                <p><strong>Order Date:</strong> {order.OrderDate.ToString("MMMM dd, yyyy")}</p>
                <p><strong>Total Amount:</strong> €{order.TotalAmount:N2}</p>
            </div>");

            // Add tracking information if available and status is shipped
            if (newStatus == OrderStatus.Shipped && !string.IsNullOrEmpty(order.TrackingNumber))
            {
                sb.AppendLine($@"
                <div class='tracking-info'>
                    <h3>🚚 Shipping Information</h3>
                    <p><strong>Tracking Number:</strong> {order.TrackingNumber}</p>
                    <p>You can track your package using the tracking number above on our carrier's website.</p>
                </div>");
            }

            // Add status-specific messages
            sb.AppendLine($@"
            <div class='status-message'>
                <h3>What this means:</h3>");

            switch (newStatus)
            {
                case OrderStatus.Verified:
                    sb.AppendLine("<p>Your order has been verified and is now being processed. We'll prepare your items for shipment.</p>");
                    break;
                case OrderStatus.Processing:
                    sb.AppendLine("<p>Your order is currently being processed. Our team is preparing your items for shipment.</p>");
                    break;
                case OrderStatus.Shipped:
                    sb.AppendLine("<p>Great news! Your order has been shipped. You should receive it within the estimated delivery time.</p>");
                    break;
                case OrderStatus.Completed:
                    sb.AppendLine("<p>Your order has been completed! Thank you for shopping with FurniCraft. We hope you enjoy your purchase!</p>");
                    break;
                case OrderStatus.Cancelled:
                    sb.AppendLine("<p>Your order has been cancelled. If this was unexpected or you have any questions, please contact our support team.</p>");
                    break;
            }

            sb.AppendLine($@"
            </div>

            <div class='next-steps'>
                <h3>Next Steps</h3>");

            if (newStatus == OrderStatus.Shipped)
            {
                sb.AppendLine("<p>• Track your package using the tracking number provided</p>");
                sb.AppendLine("<p>• Expect delivery within 3-5 business days</p>");
            }
            else if (newStatus == OrderStatus.Completed)
            {
                sb.AppendLine("<p>• Your order has been successfully delivered</p>");
                sb.AppendLine("<p>• If you have any issues, contact our support team within 30 days</p>");
            }

            sb.AppendLine($@"
                <p>• You can always check your order status by visiting your account dashboard</p>
            </div>

            <p>If you have any questions about your order, please don't hesitate to contact our customer service team.</p>
            
            <p>Thank you for choosing FurniCraft!</p>
        </div>
        
        <div class='footer'>
            <p><strong>FurniCraft</strong></p>
            <p>Rr Prshtina Re</p>
            <p>Email: support@furnicraft.com | Phone: +383 (458) 04-555</p>
            <p><a href='https://yourwebsite.com' style='color: #3b5d50;'>Visit our website</a></p>
        </div>
    </div>
</body>
</html>");

            return sb.ToString();
        }

        [HttpPost]
        public async Task<JsonResult> DeleteOrder(int id)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null)
            {
                return Json(new { success = false, message = "Order not found." });
            }

            // Remove related order details first
            var orderDetails = _context.OrderDetails.Where(od => od.OrderId == id);
            _context.OrderDetails.RemoveRange(orderDetails);

            _context.Orders.Remove(order);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Order deleted successfully." });
        }

        [HttpGet]
        public async Task<IActionResult> GetOrdersByStatus(OrderStatus status)
        {
            var orders = await _context.Orders
                .Include(o => o.User)
                .Where(o => o.Status == status)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            return PartialView("_OrderListPartial", orders);
        }
    }
}