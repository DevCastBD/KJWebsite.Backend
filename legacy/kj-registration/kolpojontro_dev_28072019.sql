-- phpMyAdmin SQL Dump
-- version 4.8.5
-- https://www.phpmyadmin.net/
--
-- Host: 127.0.0.1
-- Generation Time: Aug 27, 2019 at 02:07 PM
-- Server version: 10.1.39-MariaDB
-- PHP Version: 7.1.29

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
SET AUTOCOMMIT = 0;
START TRANSACTION;
SET time_zone = "+00:00";


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;

--
-- Database: `kolpojontro_dev`
--

-- --------------------------------------------------------

--
-- Table structure for table `aspnetroleclaims`
--

CREATE TABLE `aspnetroleclaims` (
  `Id` int(11) NOT NULL,
  `RoleId` varchar(255) NOT NULL,
  `ClaimType` longtext,
  `ClaimValue` longtext
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

-- --------------------------------------------------------

--
-- Table structure for table `aspnetroles`
--

CREATE TABLE `aspnetroles` (
  `Id` varchar(255) NOT NULL,
  `Name` varchar(256) DEFAULT NULL,
  `NormalizedName` varchar(256) DEFAULT NULL,
  `ConcurrencyStamp` longtext
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

--
-- Dumping data for table `aspnetroles`
--

INSERT INTO `aspnetroles` (`Id`, `Name`, `NormalizedName`, `ConcurrencyStamp`) VALUES
('15a114a4-d4dd-4e2c-8a57-b7a9b8d2b1d3', 'Admin', 'ADMIN', '3fffc801-703b-43a0-b946-b816fdaab4ab'),
('ad1d7635-981d-4507-a066-d26ac8af5d13', 'SuperAdmin', 'SUPERADMIN', '149e2539-a4ef-4631-8d8a-a930966c451d'),
('efeb63d1-c3a1-4dbf-89c6-371c64d2ee6d', 'Member', 'MEMBER', '8f40176d-5998-4646-9e7b-e923823cf88d');

-- --------------------------------------------------------

--
-- Table structure for table `aspnetuserclaims`
--

CREATE TABLE `aspnetuserclaims` (
  `Id` int(11) NOT NULL,
  `UserId` varchar(255) NOT NULL,
  `ClaimType` longtext,
  `ClaimValue` longtext
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

-- --------------------------------------------------------

--
-- Table structure for table `aspnetuserlogins`
--

CREATE TABLE `aspnetuserlogins` (
  `LoginProvider` varchar(255) NOT NULL,
  `ProviderKey` varchar(255) NOT NULL,
  `ProviderDisplayName` longtext,
  `UserId` varchar(255) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

-- --------------------------------------------------------

--
-- Table structure for table `aspnetuserroles`
--

CREATE TABLE `aspnetuserroles` (
  `UserId` varchar(255) NOT NULL,
  `RoleId` varchar(255) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

--
-- Dumping data for table `aspnetuserroles`
--

INSERT INTO `aspnetuserroles` (`UserId`, `RoleId`) VALUES
('691a9e8f-e890-462d-880b-7b6983db626e', 'efeb63d1-c3a1-4dbf-89c6-371c64d2ee6d'),
('b3a108f9-c21a-414d-8a40-973a2cae33bd', '15a114a4-d4dd-4e2c-8a57-b7a9b8d2b1d3'),
('b3a108f9-c21a-414d-8a40-973a2cae33bd', 'ad1d7635-981d-4507-a066-d26ac8af5d13');

-- --------------------------------------------------------

--
-- Table structure for table `aspnetusers`
--

CREATE TABLE `aspnetusers` (
  `Id` varchar(255) NOT NULL,
  `UserName` varchar(256) DEFAULT NULL,
  `NormalizedUserName` varchar(256) DEFAULT NULL,
  `Email` varchar(256) DEFAULT NULL,
  `NormalizedEmail` varchar(256) DEFAULT NULL,
  `EmailConfirmed` bit(1) NOT NULL,
  `PasswordHash` longtext,
  `SecurityStamp` longtext,
  `ConcurrencyStamp` longtext,
  `PhoneNumber` longtext,
  `PhoneNumberConfirmed` bit(1) NOT NULL,
  `TwoFactorEnabled` bit(1) NOT NULL,
  `LockoutEnd` datetime(6) DEFAULT NULL,
  `LockoutEnabled` bit(1) NOT NULL,
  `AccessFailedCount` int(11) NOT NULL,
  `FirstName` longtext,
  `LastName` longtext,
  `Gender` longtext,
  `ReasonForJoining` longtext,
  `PresentOrganization` longtext,
  `VolunteeingExperience` longtext,
  `DateOfBirth` longtext,
  `DOB` datetime(6) NOT NULL,
  `CityOfResidence` longtext,
  `CountryOfResidence` longtext,
  `PermanentAddress` longtext,
  `MailingAddress` longtext,
  `IsMailingAddressSameAsPermanentAddress` bit(1) NOT NULL,
  `BloodGroup` longtext,
  `AreasOfExpertise` longtext,
  `HighestDegree` longtext,
  `DisabilitiesIfAny` longtext,
  `Nationality` longtext,
  `PersonalWebPage` longtext,
  `SocialMediaLink` longtext,
  `Roles` longtext
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

--
-- Dumping data for table `aspnetusers`
--

INSERT INTO `aspnetusers` (`Id`, `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`, `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumber`, `PhoneNumberConfirmed`, `TwoFactorEnabled`, `LockoutEnd`, `LockoutEnabled`, `AccessFailedCount`, `FirstName`, `LastName`, `Gender`, `ReasonForJoining`, `PresentOrganization`, `VolunteeingExperience`, `DateOfBirth`, `DOB`, `CityOfResidence`, `CountryOfResidence`, `PermanentAddress`, `MailingAddress`, `IsMailingAddressSameAsPermanentAddress`, `BloodGroup`, `AreasOfExpertise`, `HighestDegree`, `DisabilitiesIfAny`, `Nationality`, `PersonalWebPage`, `SocialMediaLink`, `Roles`) VALUES
('691a9e8f-e890-462d-880b-7b6983db626e', 'user', 'USER', 'user1@kol.com', 'USER1@KOL.COM', b'0', 'AQAAAAEAACcQAAAAELUjuU7cD+wkk/3KLlciAZKPypFkVNGhMtl48Wmz8neAvubdL8q9zlEx77TJ1TtBBw==', 'HMUDZ2NAP6S4Y2TPNSBQYTUWK6DBG5FS', '2bfb0f18-aae6-4112-9327-dffc2a8b1924', NULL, b'0', b'0', NULL, b'1', 0, 'New User 2 First', 'New User 2 Last', 'Male', 'Because of Passion', 'Amra Korbo Joy', '1 years', '07/19/1996', '0001-01-01 00:00:00.000000', 'Khulna', 'BD', NULL, NULL, b'0', NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'Member'),
('b3a108f9-c21a-414d-8a40-973a2cae33bd', 'superuser', 'SUPERUSER', 'admin@admin.com', 'ADMIN@ADMIN.COM', b'0', 'AQAAAAEAACcQAAAAECPiNJ3seWghGUpUfS4ino7CxyFVP36XGhnY5isjXBhFdpUROrJKEB5j+wnG7fvMwA==', 'BW3JF2SXEIISFT5HTVCT2CFXD3X2Q7XP', 'f30a7b3b-e001-40b3-b417-04ce0a40d105', NULL, b'0', b'0', NULL, b'1', 0, 'New User 2 First', 'New User 2 Last', 'Male', 'Because of Passion', 'Amra Korbo Joy', '1 years', '07/19/1996', '0001-01-01 00:00:00.000000', 'Khulna', 'BD', NULL, NULL, b'0', NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'SuperAdmin,Admin');

-- --------------------------------------------------------

--
-- Table structure for table `aspnetusertokens`
--

CREATE TABLE `aspnetusertokens` (
  `UserId` varchar(255) NOT NULL,
  `LoginProvider` varchar(255) NOT NULL,
  `Name` varchar(255) NOT NULL,
  `Value` longtext
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

-- --------------------------------------------------------

--
-- Table structure for table `awaitingusers`
--

CREATE TABLE `awaitingusers` (
  `Id` int(11) NOT NULL,
  `FirstName` longtext,
  `LastName` longtext,
  `Gender` longtext,
  `ReasonForJoining` longtext,
  `PresentOrganization` longtext,
  `VolunteeingExperience` longtext,
  `DateOfBirth` longtext,
  `DOB` datetime(6) NOT NULL,
  `CityOfResidence` longtext,
  `CountryOfResidence` longtext,
  `Status` longtext,
  `StatusLastUpdatedAt` datetime(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

--
-- Dumping data for table `awaitingusers`
--

INSERT INTO `awaitingusers` (`Id`, `FirstName`, `LastName`, `Gender`, `ReasonForJoining`, `PresentOrganization`, `VolunteeingExperience`, `DateOfBirth`, `DOB`, `CityOfResidence`, `CountryOfResidence`, `Status`, `StatusLastUpdatedAt`) VALUES
(1, 'id ut ', 'laborum ad reprehenderit', 'sed aute Duis nostrud', 'nulla deserunt', 'quis dolor anim', 'ess', '1998-03-15T14:02:27.857Z', '1998-03-15 20:02:27.857000', 'et sunt aliqua sint', 'enim magna ex sint', 'Awaiting', '2019-08-27 16:37:50.572574');

--
-- Indexes for dumped tables
--

--
-- Indexes for table `aspnetroleclaims`
--
ALTER TABLE `aspnetroleclaims`
  ADD PRIMARY KEY (`Id`),
  ADD KEY `IX_AspNetRoleClaims_RoleId` (`RoleId`);

--
-- Indexes for table `aspnetroles`
--
ALTER TABLE `aspnetroles`
  ADD PRIMARY KEY (`Id`),
  ADD UNIQUE KEY `RoleNameIndex` (`NormalizedName`);

--
-- Indexes for table `aspnetuserclaims`
--
ALTER TABLE `aspnetuserclaims`
  ADD PRIMARY KEY (`Id`),
  ADD KEY `IX_AspNetUserClaims_UserId` (`UserId`);

--
-- Indexes for table `aspnetuserlogins`
--
ALTER TABLE `aspnetuserlogins`
  ADD PRIMARY KEY (`LoginProvider`,`ProviderKey`),
  ADD KEY `IX_AspNetUserLogins_UserId` (`UserId`);

--
-- Indexes for table `aspnetuserroles`
--
ALTER TABLE `aspnetuserroles`
  ADD PRIMARY KEY (`UserId`,`RoleId`),
  ADD KEY `IX_AspNetUserRoles_RoleId` (`RoleId`);

--
-- Indexes for table `aspnetusers`
--
ALTER TABLE `aspnetusers`
  ADD PRIMARY KEY (`Id`),
  ADD UNIQUE KEY `UserNameIndex` (`NormalizedUserName`),
  ADD KEY `EmailIndex` (`NormalizedEmail`);

--
-- Indexes for table `aspnetusertokens`
--
ALTER TABLE `aspnetusertokens`
  ADD PRIMARY KEY (`UserId`,`LoginProvider`,`Name`);

--
-- Indexes for table `awaitingusers`
--
ALTER TABLE `awaitingusers`
  ADD PRIMARY KEY (`Id`);

--
-- AUTO_INCREMENT for dumped tables
--

--
-- AUTO_INCREMENT for table `aspnetroleclaims`
--
ALTER TABLE `aspnetroleclaims`
  MODIFY `Id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `aspnetuserclaims`
--
ALTER TABLE `aspnetuserclaims`
  MODIFY `Id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `awaitingusers`
--
ALTER TABLE `awaitingusers`
  MODIFY `Id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=2;

--
-- Constraints for dumped tables
--

--
-- Constraints for table `aspnetroleclaims`
--
ALTER TABLE `aspnetroleclaims`
  ADD CONSTRAINT `FK_AspNetRoleClaims_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `aspnetroles` (`Id`) ON DELETE CASCADE;

--
-- Constraints for table `aspnetuserclaims`
--
ALTER TABLE `aspnetuserclaims`
  ADD CONSTRAINT `FK_AspNetUserClaims_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `aspnetusers` (`Id`) ON DELETE CASCADE;

--
-- Constraints for table `aspnetuserlogins`
--
ALTER TABLE `aspnetuserlogins`
  ADD CONSTRAINT `FK_AspNetUserLogins_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `aspnetusers` (`Id`) ON DELETE CASCADE;

--
-- Constraints for table `aspnetuserroles`
--
ALTER TABLE `aspnetuserroles`
  ADD CONSTRAINT `FK_AspNetUserRoles_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `aspnetroles` (`Id`) ON DELETE CASCADE,
  ADD CONSTRAINT `FK_AspNetUserRoles_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `aspnetusers` (`Id`) ON DELETE CASCADE;

--
-- Constraints for table `aspnetusertokens`
--
ALTER TABLE `aspnetusertokens`
  ADD CONSTRAINT `FK_AspNetUserTokens_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `aspnetusers` (`Id`) ON DELETE CASCADE;
COMMIT;

/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
