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

namespace IO.Swagger.Test
{
    /// <summary>
    ///  Class for testing ZonesApi
    /// </summary>
    /// <remarks>
    /// This file is automatically generated by Swagger Codegen.
    /// Please update the test case below to test the API endpoint.
    /// </remarks>
    [TestFixture]
    public class ZonesApiTests
    {
        private ZonesApi instance;

        /// <summary>
        /// Setup before each unit test
        /// </summary>
        [SetUp]
        public void Init()
        {
            instance = new ZonesApi();
        }

        /// <summary>
        /// Clean up after each unit test
        /// </summary>
        [TearDown]
        public void Cleanup()
        {

        }

        /// <summary>
        /// Test an instance of ZonesApi
        /// </summary>
        [Test]
        public void InstanceTest()
        {
            // TODO uncomment below to test 'IsInstanceOfType' ZonesApi
            //Assert.IsInstanceOfType(typeof(ZonesApi), instance, "instance is a ZonesApi");
        }

        
        /// <summary>
        /// Test GetZones
        /// </summary>
        [Test]
        public void GetZonesTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //var response = instance.GetZones();
            //Assert.IsInstanceOf<List<string>> (response, "response is List<string>");
        }
        
        /// <summary>
        /// Test QuietZone
        /// </summary>
        [Test]
        public void QuietZoneTest()
        {
            // TODO uncomment below to test the method and replace null with proper value
            //string zoneId = null;
            //instance.QuietZone(zoneId);
            
        }
        
    }

}