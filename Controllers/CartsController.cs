using FurniCraft.Data;
using FurniCraft.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FurniCraft.Controllers
{
    [Authorize]
    public class CartsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<IdentityUser> _userManager;

        public CartsController(ApplicationDbContext context, UserManager<IdentityUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var userId = _userManager.GetUserId(User);
            var cartItems = await _context.ShoppingCarts
                .Include(c => c.Product)
                .Where(c => c.UserId == userId)
                .ToListAsync();

            var totalAmount = cartItems.Sum(c => c.Product.Price * c.Qty);

            var model = new ShoppingCartDetails
            {
                Carts = cartItems,
                TotalAmount = totalAmount,
                IsEmpty = !cartItems.Any() // Add a flag to indicate if the cart is empty
            };

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> AddToCart(int ProId, int qty)
        {
            var userId = _userManager.GetUserId(User);
            var cartItem = await _context.ShoppingCarts
                .FirstOrDefaultAsync(c => c.UserId == userId && c.ProId == ProId);

            if (cartItem == null)
            {
                cartItem = new ShoppingCart
                {
                    UserId = userId,
                    ProId = ProId,
                    Qty = qty
                };
                _context.ShoppingCarts.Add(cartItem);
            }
            else
            {
                cartItem.Qty += qty;
            }

            await _context.SaveChangesAsync();

            // Get the updated cart item count
            var cartItemCount = await _context.ShoppingCarts
                .Where(c => c.UserId == userId)
                .SumAsync(c => c.Qty);

            return Json(new { success = true, cartItemCount = cartItemCount });
        }
        [HttpPost]
        public async Task<IActionResult> RemoveFromCart(int CartId)
        {
            var cartItem = await _context.ShoppingCarts.FindAsync(CartId);
            if (cartItem != null)
            {
                _context.ShoppingCarts.Remove(cartItem);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Checkout()
        {
            var userId = _userManager.GetUserId(User);
            var cartItems = await _context.ShoppingCarts
                .Include(c => c.Product)
                .Where(c => c.UserId == userId)
                .ToListAsync();

            var totalAmount = cartItems.Sum(c => c.Product.Price * c.Qty);

            ViewBag.Carts = cartItems;
            ViewBag.TotalAmount = totalAmount;

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Checkout(Order order)
        {
            var userId = _userManager.GetUserId(User);
            var cartItems = await _context.ShoppingCarts
                .Include(c => c.Product)
                .Where(c => c.UserId == userId)
                .ToListAsync();

            if (!cartItems.Any())
            {
                ModelState.AddModelError("", "Your cart is empty.");
                return View(order);
            }

            order.UserId = userId;
            order.OrderDate = DateTime.Now;
            order.TotalAmount = cartItems.Sum(c => c.Product.Price * c.Qty);

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            foreach (var cartItem in cartItems)
            {
                var orderDetail = new OrderDetail
                {
                    OrderId = order.OrderId,
                    ProId = cartItem.ProId,
                    Quantity = cartItem.Qty,
                    Price = cartItem.Product.Price
                };
                _context.OrderDetails.Add(orderDetail);
            }

            _context.ShoppingCarts.RemoveRange(cartItems);
            await _context.SaveChangesAsync();

            return RedirectToAction("OrderConfirmation", new { id = order.OrderId });
        }

        public IActionResult OrderConfirmation(int id)
        {
            var order = _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails)
                .ThenInclude(od => od.Product)
                .FirstOrDefault(o => o.OrderId == id);

            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }
    }
}
