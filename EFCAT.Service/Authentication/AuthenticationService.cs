﻿using EFCAT.Model.Annotation;
using EFCAT.Model.Data;
using EFCAT.Model.Data.Annotation;
using EFCAT.Model.Extension;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace EFCAT.Service.Authentication;


public interface IAuthService<TAccount> where TAccount : class {
    Task<bool> LoginAsync(object value);
    bool Login(object value);
    Task<bool> RegisterAsync(TAccount account);
    bool Register(TAccount account);
    Task LogoutAsync();
    void Logout();
    TAccount? GetAccount();
}

public abstract class AuthenticationService<TAccount> : AuthenticationStateProvider, IAuthService<TAccount> where TAccount : class {
    readonly string _itemName;
    readonly string _key;

    DbContext _dbContext;
    DbSet<TAccount> _dbSet;

    AuthenticationState _authenticationState = new AuthenticationState(new ClaimsPrincipal());

    TAccount? account;

    public AuthenticationService() {
        AuthenticationSettings.provider = this;
    }
    public AuthenticationService(DbContext dbContext, string itemName = "AuthenticationToken", string key = "keyforjwtcreationefcatdef") : this() {
        _dbContext = dbContext;
        _dbSet = dbContext.Set<TAccount>();
        _itemName = itemName;
        _key = key;
    }

    public async override Task<AuthenticationState> GetAuthenticationStateAsync() {
        try {
            string token = await ReadAsync(_itemName);
            if (string.IsNullOrEmpty(token)) return _authenticationState;

            OnAuthentication(token);

            _authenticationState = await GetAuthState(token);
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("Authentication Error: " + ex.Message);
        }
        return _authenticationState;
    }
    private async Task<AuthenticationState> GetAuthState(string token) {
        AuthenticationState state = GetAuthentication(token);
        Claim? identityClaim = state.User.Claims.FirstOrDefault(c => c.Type == "_identity");
        if (identityClaim == null) return _authenticationState;
        Dictionary<string, object> identity = (Dictionary<string, object>)JsonSerializer.Deserialize(identityClaim.Value, typeof(Dictionary<string, object>));
        ParameterExpression parameter = Expression.Parameter(typeof(TAccount), "entity");
        List<BinaryExpression> expressions = new List<BinaryExpression>();
        foreach (var property in identity) {
            PropertyInfo? info = typeof(TAccount).GetProperty(property.Key);
            if (info == null) return _authenticationState;
            // entity.Property
            MemberExpression member = Expression.MakeMemberAccess(parameter, info);
            // value
            ConstantExpression constant = Expression.Constant(Convert.ChangeType(((JsonElement)property.Value).GetRawText(), info.PropertyType));
            // entity.Property == value
            expressions.Add(Expression.Equal(member, constant));
        }
        if (!expressions.Any()) return _authenticationState;
        Expression final = expressions.Aggregate((left, right) => Expression.And(left, right));
        // entity => entity.Property == value && ...
        LambdaExpression lambda = Expression.Lambda(final, parameter);
        var result = await _dbSet.AsQueryable<TAccount>().Where((Expression<Func<TAccount, bool>>)lambda).ToListAsync();

        if (result == null || !result.Any()) return _authenticationState;

        TAccount? account = result.FirstOrDefault();
        if (account == null) return _authenticationState;
        this.account = account;
        return state;
    }
    protected virtual void OnAuthentication(string token) { }

    public async Task<bool> LoginAsync(object obj) {
        // Check if the Object is a Class
        if (!obj.GetType().IsClass) throw (new Exception($"Object needs to be a class!"));
        // Check if Entity has Properties
        if (obj.GetType().GetProperties().Length == 0) throw new Exception($"Class has no properties!");

        Console.WriteLine();

        IEnumerable<TAccount> resultQuery;

        // Get the Expression from all non VOs
        Expression? query = GetExpression(obj, false);

        // Filter the Account on Queryable Methods
        if (query == null) resultQuery = await _dbSet.AsQueryable<TAccount>().Where(a => 1 == 1).ToListAsync();
        else resultQuery = await _dbSet.AsQueryable<TAccount>().Where((Expression<Func<TAccount, bool>>)query).ToListAsync();

        // Return false if there are no accounts
        if (resultQuery == null || !resultQuery.Any()) return false;

        IEnumerable<TAccount> resultEnumerable;

        Expression? enumerable = GetExpression(obj, true);

        if (enumerable == null) resultEnumerable = resultQuery;
        else resultEnumerable = resultQuery.AsQueryable().Where((Expression<Func<TAccount, bool>>)enumerable).AsEnumerable();

        if (resultEnumerable == null || !resultEnumerable.Any()) return false;

        TAccount? account = resultEnumerable.FirstOrDefault();
        if (account == null) return false;
        await SetAccountAsync(account);
        return true;
    }
    public bool Login(object obj) => Task.Run(() => LoginAsync(obj)).Result;

    private Expression? GetExpression(object obj, bool vos) {
        // entity
        ParameterExpression parameter = Expression.Parameter(typeof(TAccount), "entity");
        List<Expression> propertyExpressions = new List<Expression>();
        // Iterate over every Property
        foreach (var property in obj.GetType().GetProperties()) {
            // Get the Value of the Property
            var value = property.GetValue(obj, null);
            if (value == null) continue;
            // Check if the Attribute has Substitute
            property.OnAttribute<SubstituteAttribute>(attr => {
                List<Expression> binaryExpressions = new List<Expression>();
                foreach (string name in attr.ColumnNames) {
                    // Add Binary to to Property Binarys
                    if (GetPropertyExpression(parameter, name, value, vos) is Expression exp) binaryExpressions.Add(exp);
                }
                if (binaryExpressions.Any()) propertyExpressions.Add(binaryExpressions.Aggregate((left, right) => Expression.Or(left, right)));
            }, () => {
                if (GetPropertyExpression(parameter, property.Name, value, vos) is Expression exp) propertyExpressions.Add(exp);
            });
        }
        if (!propertyExpressions.Any()) return null;
        Expression final = propertyExpressions.Aggregate((left, right) => Expression.And(left, right));
        // entity => entity.Property == value && ...
        LambdaExpression lambda = Expression.Lambda(final, parameter);
        return lambda;
    }
    private Expression? GetPropertyExpression(ParameterExpression parameter, string name, object value, bool vos) {
        // Get PropertyInfo
        PropertyInfo? info = typeof(TAccount).GetProperty(name);
        if (info == null) throw (new Exception($"{name} does not exist in {typeof(TAccount).Name}!"));
        if (info.PropertyType.BaseType == typeof(ValueObject) && !vos) return null;
        else if (info.PropertyType.BaseType != typeof(ValueObject) && vos) return null;
        // Check if Property is unique
        if (info.HasAttribute<UniqueAttribute>()) {
            // entity.Property
            MemberExpression member = Expression.MakeMemberAccess(parameter, info);
            // Normalize entity.Property
            MethodCallExpression normalizedMember = Expression.Call(member, "ToUpper", null);
            // value
            ConstantExpression constant = Expression.Constant(Convert.ChangeType(value.ToString().ToUpper(), info.PropertyType));
            // entity.Property == value
            return Expression.Equal(normalizedMember, constant);
        } else if (info.PropertyType.IsGenericType && info.PropertyType.GetGenericTypeDefinition() == typeof(Crypt<>)) {
            // entity.Property
            MemberExpression member = Expression.MakeMemberAccess(parameter, info);
            // value
            ConstantExpression constant = Expression.Constant(Convert.ChangeType(value, typeof(string)));
            // entity.Property == value
            return Expression.Call(member, info.PropertyType.GetMethod("Verify"), constant);
        } else {
            // entity.Property
            MemberExpression member = Expression.MakeMemberAccess(parameter, info);
            // value
            ConstantExpression constant = Expression.Constant(Convert.ChangeType(value, info.PropertyType));
            // entity.Property == value
            return Expression.Equal(member, constant);
        }
    }
    private async Task SetAccountAsync(TAccount account) {
        Dictionary<string, object> identity = new Dictionary<string, object>();
        foreach (PropertyInfo property in account.GetType().GetProperties()) {
            if (property.HasAttribute<PrimaryKeyAttribute>() || property.HasAttribute<System.ComponentModel.DataAnnotations.KeyAttribute>()) {
                var value = account.GetType().GetProperty(property.Name).GetValue(account, null);
                identity.Add(property.Name, value);
            }
        }
        string token = GenerateToken(identity);
        _ = WriteAsync(_itemName, token).ConfigureAwait(true);
    }

    public async Task<bool> RegisterAsync(TAccount account) {
        if (_dbSet.Contains(account)) return false;
        await _dbSet.AddAsync(account);
        await _dbContext.SaveChangesAsync();
        return account != null;
    }
    public bool Register(TAccount account) => Task.Run(() => RegisterAsync(account)).Result;

    public async Task LogoutAsync() => await RemoveAsync(_itemName);
    public void Logout() => Task.Run(() => LogoutAsync()).ConfigureAwait(false);

    public TAccount? GetAccount() => account ?? null;

    protected abstract Task<string> ReadAsync(string item);
    protected abstract Task WriteAsync(string item, string value);
    protected abstract Task RemoveAsync(string item);

    private AuthenticationState GetAuthentication(string token) => new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(ParseClaimsFromJwt(token), "Authentication")));

    private string GenerateToken(Dictionary<string, object> claims) {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            "localhost",
            "localhost",
            new[] { new Claim("_identity", JsonSerializer.Serialize(claims, typeof(Dictionary<string, object>))) },
            expires: DateTime.Now.AddDays(7),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    private IEnumerable<Claim> ParseClaimsFromJwt(string jwt) {
        var claims = new List<Claim>();
        var payload = jwt.Split('.')[1];
        var jsonBytes = ParseBase64WithoutPadding(payload);
        var keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

        keyValuePairs.TryGetValue(ClaimTypes.Role, out object roles);

        if (roles != null) {
            if (roles.ToString().Trim().StartsWith("[")) {
                var parsedRoles = JsonSerializer.Deserialize<string[]>(roles.ToString());

                foreach (var parsedRole in parsedRoles) {
                    claims.Add(new Claim(ClaimTypes.Role, parsedRole));
                }
            } else {
                claims.Add(new Claim(ClaimTypes.Role, roles.ToString()));
            }

            keyValuePairs.Remove(ClaimTypes.Role);
        }

        claims.AddRange(keyValuePairs.Select(kvp => new Claim(kvp.Key, kvp.Value.ToString())));

        return claims;
    }
    private byte[] ParseBase64WithoutPadding(string base64) {
        switch (base64.Length % 4) {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    protected abstract Crypt<IAlgorithm> Crypt { get; set; }