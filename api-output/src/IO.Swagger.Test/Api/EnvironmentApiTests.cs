/* 
 * home-iot-api
 *
 * The API for the EatBacon IOT project
 *
 * OpenAPI spec version: 1.0.0
 * 
 * Generated by: https://github.com/swagger-api/swagger-codegen.git
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using RestSharp;
using NUnit.Framework;

using IO.Swagger.Client;
using IO.Swagger.Api;
using IO.Swagger.Model;

namespace IO.Swagger.Test
{
    /// <summary>
    ///  Class for testing EnvironmentApi
    /// </summary>
    /// <remarks>
    /// This file is automatically generated by Swagger Codegen.
    /// Please update the test case below to test the API endpoint.
    /// </remarks>
    [TestFixture]
    public class EnvironmentApiTests
    {
        private EnvironmentApi instance;

        /// <summary>
        /// Setup before each unit test
        /// </summary>
        [SetUp]
        public void Init()
        {
            instance = new EnvironmentApi();
        }

        /// <summary>
        /// Clean up after each unit test
        /// </summary>
        [TearDown]
        public void Cleanup()
        {

        }

        /// <summary>
        /// Test an instance of EnvironmentApi
        /// </summary>
        [Test]
        public void InstanceTest()
        {
            // TODO uncomment below to test 'IsInstanceOfType' EnvironmentApi
            //Assert.IsInstanceOfType(typeof(EnvironmentApi), instance, "instance is a EnvironmentApi");
        }

        
        /// <summary>
        /// Test GetForecast
        /// </summary>
        [Test]
        public void GetForecastTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //int? days = null;
            //var response = instance.GetForecast(days);
            //Assert.IsInstanceOf<ForecastResponse> (response, "response is ForecastResponse");
        }
        
        /// <summary>
        /// Test GetHeaterState
        /// </summary>
        [Test]
        public void GetHeaterStateTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //string zoneId = null;
            //var response = instance.GetHeaterState(zoneId);
            //Assert.IsInstanceOf<HeaterState> (response, "response is HeaterState");
        }
        
        /// <summary>
        /// Test GetZoneTemperature
        /// </summary>
        [Test]
        public void GetZoneTemperatureTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //string zoneId = null;
            //var response = instance.GetZoneTemperature(zoneId);
            //Assert.IsInstanceOf<TemperatueZoneStatus> (response, "response is TemperatueZoneStatus");
        }
        
        /// <summary>
        /// Test SetHeaterState
        /// </summary>
        [Test]
        public void SetHeaterStateTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //string zoneId = null;
            //string state = null;
            //var response = instance.SetHeaterState(zoneId, state);
            //Assert.IsInstanceOf<ApiResponse> (response, "response is ApiResponse");
        }
        
        /// <summary>
        /// Test TemperatureSummary
        /// </summary>
        [Test]
        public void TemperatureSummaryTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //var response = instance.TemperatureSummary();
            //Assert.IsInstanceOf<TemperatureSummary> (response, "response is TemperatureSummary");
        }
        
    }

}
