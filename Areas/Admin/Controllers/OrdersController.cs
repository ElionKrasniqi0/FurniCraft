using FurniCraft.Data;
using FurniCraft.Enum;
using FurniCraft.Models;
using FurniCraft.ViewModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System; // Add this using statement

namespace FurniCraft.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class OrdersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public OrdersController(ApplicationDbContext context)
        {
            _context = context;
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
        public async Task<IActionResult> UpdateStatus(int id, OrderStatus status, string trackingNumber = "", string adminNotes = "")
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null)
            {
                return Json(new { success = false, message = "Order not found." });
            }

            order.Status = status;

            // FIX: Handle null values
            order.TrackingNumber = trackingNumber ?? string.Empty;
            order.AdminNotes = adminNotes ?? string.Empty;

            // Update status dates
            switch (status)
            {
                case OrderStatus.Verified:
                    order.VerifiedDate = DateTime.Now;
                    break;
                case OrderStatus.Processing:
                    order.ProcessingDate = DateTime.Now;
                    break;
                case OrderStatus.Shipped:
                    order.ShippedDate = DateTime.Now;
                    break;
                case OrderStatus.Completed:
                    order.CompletedDate = DateTime.Now;
                    break;
                case OrderStatus.Cancelled:
                    order.CancelledDate = DateTime.Now;
                    break;
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Order status updated successfully." });
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
